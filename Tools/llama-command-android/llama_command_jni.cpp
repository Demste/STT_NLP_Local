#include <jni.h>

#include <android/log.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <exception>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "llama.h"

namespace {

constexpr const char* kLogTag = "llama_command";

struct LlamaCommandContext {
    llama_model* model = nullptr;
    llama_context* ctx = nullptr;
    const llama_vocab* vocab = nullptr;
    int context_size = 0;
    int last_prompt_tokens = 0;
    int last_output_tokens = 0;
    int64_t last_prompt_eval_ms = 0;
    int64_t last_first_token_ms = 0;
    int64_t last_generation_ms = 0;
    int64_t last_total_ms = 0;
    bool last_json_early_stop = false;
    std::mutex mutex;
};

using SteadyClock = std::chrono::steady_clock;

int64_t elapsed_ms(SteadyClock::time_point started) {
    return std::chrono::duration_cast<std::chrono::milliseconds>(SteadyClock::now() - started).count();
}

size_t balanced_root_end(const std::string& value) {
    size_t start = value.find_first_not_of(" \t\r\n");
    if (start == std::string::npos || value[start] != '{') {
        return std::string::npos;
    }
    int depth = 0;
    bool in_string = false;
    bool escaped = false;
    for (size_t i = start; i < value.size(); ++i) {
        const char c = value[i];
        if (escaped) {
            escaped = false;
            continue;
        }
        if (c == '\\' && in_string) {
            escaped = true;
            continue;
        }
        if (c == '"') {
            in_string = !in_string;
            continue;
        }
        if (in_string) continue;
        if (c == '{') ++depth;
        if (c == '}' && --depth == 0) return i;
        if (depth < 0) return std::string::npos;
    }
    return std::string::npos;
}

void reset_metrics(LlamaCommandContext* state) {
    state->last_prompt_tokens = 0;
    state->last_output_tokens = 0;
    state->last_prompt_eval_ms = 0;
    state->last_first_token_ms = 0;
    state->last_generation_ms = 0;
    state->last_total_ms = 0;
    state->last_json_early_stop = false;
}

std::string jstring_to_utf8(JNIEnv* env, jstring value) {
    if (value == nullptr) {
        return "";
    }

    const char* chars = env->GetStringUTFChars(value, nullptr);
    if (chars == nullptr) {
        return "";
    }

    std::string result(chars);
    env->ReleaseStringUTFChars(value, chars);
    return result;
}

void free_context(LlamaCommandContext* state) {
    if (state == nullptr) {
        return;
    }
    if (state->ctx != nullptr) {
        llama_free(state->ctx);
        state->ctx = nullptr;
    }
    if (state->model != nullptr) {
        llama_model_free(state->model);
        state->model = nullptr;
    }
    delete state;
}

void throw_java(JNIEnv* env, const char* message) {
    jclass cls = env->FindClass("java/lang/IllegalStateException");
    if (cls != nullptr) {
        env->ThrowNew(cls, message != nullptr ? message : "llama_command native error");
    }
}

std::vector<llama_token> tokenize(LlamaCommandContext* state, const std::string& prompt) {
    const int estimate = static_cast<int>(prompt.size()) + 8;
    std::vector<llama_token> tokens(static_cast<size_t>(std::max(estimate, 32)));

    int count = llama_tokenize(
        state->vocab,
        prompt.c_str(),
        static_cast<int32_t>(prompt.size()),
        tokens.data(),
        static_cast<int32_t>(tokens.size()),
        true,
        true);

    if (count < 0) {
        tokens.resize(static_cast<size_t>(-count));
        count = llama_tokenize(
            state->vocab,
            prompt.c_str(),
            static_cast<int32_t>(prompt.size()),
            tokens.data(),
            static_cast<int32_t>(tokens.size()),
            true,
            true);
    }

    if (count < 0) {
        throw std::runtime_error("llama_tokenize failed");
    }

    tokens.resize(static_cast<size_t>(count));
    return tokens;
}

std::string token_to_piece(LlamaCommandContext* state, llama_token token) {
    std::vector<char> piece(32);
    int count = llama_token_to_piece(
        state->vocab,
        token,
        piece.data(),
        static_cast<int32_t>(piece.size()),
        0,
        true);

    if (count < 0) {
        piece.resize(static_cast<size_t>(-count));
        count = llama_token_to_piece(
            state->vocab,
            token,
            piece.data(),
            static_cast<int32_t>(piece.size()),
            0,
            true);
    }

    if (count <= 0) {
        return "";
    }

    return std::string(piece.data(), static_cast<size_t>(count));
}

}  // namespace

extern "C" JNIEXPORT jlong JNICALL
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeInit(
    JNIEnv* env,
    jclass,
    jstring model_path,
    jint context_size,
    jint threads) {
    try {
        std::string path = jstring_to_utf8(env, model_path);
        if (path.empty()) {
            throw_java(env, "modelPath is empty");
            return 0;
        }

        llama_backend_init();

        llama_model_params model_params = llama_model_default_params();
        llama_model* model = llama_model_load_from_file(path.c_str(), model_params);
        if (model == nullptr) {
            throw_java(env, "llama_model_load_from_file failed");
            return 0;
        }

        llama_context_params ctx_params = llama_context_default_params();
        ctx_params.n_ctx = static_cast<uint32_t>(std::max<int>(context_size, 512));
        ctx_params.n_threads = std::max<int>(threads, 1);
        ctx_params.n_threads_batch = std::max<int>(threads, 1);

        llama_context* ctx = llama_new_context_with_model(model, ctx_params);
        if (ctx == nullptr) {
            llama_model_free(model);
            throw_java(env, "llama_new_context_with_model failed");
            return 0;
        }

        auto* state = new LlamaCommandContext();
        state->model = model;
        state->ctx = ctx;
        state->vocab = llama_model_get_vocab(model);
        state->context_size = static_cast<int>(ctx_params.n_ctx);

        __android_log_print(
            ANDROID_LOG_INFO,
            kLogTag,
            "Initialized model: %s n_ctx=%d threads=%d",
            path.c_str(),
            state->context_size,
            ctx_params.n_threads);
        return reinterpret_cast<jlong>(state);
    } catch (const std::exception& exc) {
        throw_java(env, exc.what());
        return 0;
    } catch (...) {
        throw_java(env, "nativeInit failed");
        return 0;
    }
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeGenerate(
    JNIEnv* env,
    jclass,
    jlong handle,
    jstring prompt_value,
    jint max_tokens,
    jfloat temperature,
    jfloat top_p,
    jint top_k,
    jstring grammar_value) {
    auto* state = reinterpret_cast<LlamaCommandContext*>(handle);
    if (state == nullptr || state->ctx == nullptr || state->model == nullptr) {
        return env->NewStringUTF("NATIVE_ERROR: nativeGenerate called with invalid handle");
    }

    std::lock_guard<std::mutex> lock(state->mutex);

    try {
        reset_metrics(state);
        const auto total_started = SteadyClock::now();
        std::string prompt = jstring_to_utf8(env, prompt_value);
        std::string grammar = jstring_to_utf8(env, grammar_value);
        std::vector<llama_token> tokens = tokenize(state, prompt);
        const int limit = std::max<int>(1, max_tokens);
        const int safety_margin = 32;
        const int prompt_tokens = static_cast<int>(tokens.size());
        state->last_prompt_tokens = prompt_tokens;
        const int context_size = std::max<int>(state->context_size, 512);

        __android_log_print(
            ANDROID_LOG_INFO,
            kLogTag,
            "nativeGenerate prompt_chars=%zu prompt_tokens=%d n_ctx=%d max_tokens=%d temperature=%.3f top_p=%.3f top_k=%d",
            prompt.size(),
            prompt_tokens,
            context_size,
            limit,
            static_cast<double>(temperature),
            static_cast<double>(top_p),
            static_cast<int>(top_k));

        if (prompt_tokens <= 0) {
            return env->NewStringUTF("NATIVE_ERROR: empty_prompt_tokens");
        }

        if (prompt_tokens >= context_size - limit - safety_margin) {
            std::string error = "NATIVE_ERROR: prompt_too_long prompt_tokens="
                + std::to_string(prompt_tokens)
                + " n_ctx="
                + std::to_string(context_size)
                + " max_tokens="
                + std::to_string(limit)
                + " safety_margin="
                + std::to_string(safety_margin);
            __android_log_print(ANDROID_LOG_ERROR, kLogTag, "%s", error.c_str());
            return env->NewStringUTF(error.c_str());
        }

        llama_memory_clear(llama_get_memory(state->ctx), true);

        const auto prompt_eval_started = SteadyClock::now();
        llama_batch batch = llama_batch_get_one(tokens.data(), static_cast<int32_t>(tokens.size()));
        if (llama_decode(state->ctx, batch) != 0) {
            return env->NewStringUTF("NATIVE_ERROR: llama_decode_failed_for_prompt");
        }
        state->last_prompt_eval_ms = elapsed_ms(prompt_eval_started);

        std::string output;
        float temp = std::max<float>(0.0f, temperature);
        float nucleus = std::max<float>(0.05f, std::min<float>(1.0f, top_p));
        int k = std::max<int>(1, top_k);

        llama_sampler_chain_params sampler_params = llama_sampler_chain_default_params();
        std::unique_ptr<llama_sampler, decltype(&llama_sampler_free)> sampler(
            llama_sampler_chain_init(sampler_params),
            llama_sampler_free);
        if (sampler.get() == nullptr) {
            throw_java(env, "llama_sampler_chain_init failed");
            return nullptr;
        }

        if (!grammar.empty()) {
            llama_sampler* grammar_sampler = llama_sampler_init_grammar(
                state->vocab, grammar.c_str(), "root");
            if (grammar_sampler == nullptr) {
                return env->NewStringUTF("NATIVE_ERROR: invalid_grammar");
            }
            llama_sampler_chain_add(sampler.get(), grammar_sampler);
        }

        if (temp <= 0.0f) {
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_greedy());
        } else {
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_k(k));
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_p(nucleus, 1));
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_temp(temp));
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_dist(LLAMA_DEFAULT_SEED));
        }

        const auto generation_started = SteadyClock::now();
        for (int i = 0; i < limit; ++i) {
            llama_token next = llama_sampler_sample(sampler.get(), state->ctx, -1);
            state->last_output_tokens = i + 1;

            if (i == 0) {
                state->last_first_token_ms = elapsed_ms(generation_started);
                __android_log_print(
                    ANDROID_LOG_INFO,
                    kLogTag,
                    "nativeGenerate first_token_id=%d is_eog=%d",
                    static_cast<int>(next),
                    llama_vocab_is_eog(state->vocab, next) ? 1 : 0);
            }

            if (llama_vocab_is_eog(state->vocab, next)) {
                if (output.empty()) {
                    return env->NewStringUTF("NATIVE_ERROR: empty_generation_eog_first_token");
                }
                break;
            }

            output += token_to_piece(state, next);

            const size_t json_end = balanced_root_end(output);
            if (json_end != std::string::npos) {
                output.resize(json_end + 1);
                state->last_json_early_stop = true;
                break;
            }

            llama_token next_tokens[] = {next};
            llama_batch next_batch = llama_batch_get_one(next_tokens, 1);
            if (llama_decode(state->ctx, next_batch) != 0) {
                return env->NewStringUTF("NATIVE_ERROR: llama_decode_failed_during_generation");
            }
        }

        state->last_generation_ms = elapsed_ms(generation_started);
        state->last_total_ms = elapsed_ms(total_started);

        __android_log_print(
            ANDROID_LOG_INFO,
            kLogTag,
            "nativeGenerate output_chars=%zu output_tokens=%d prompt_eval_ms=%lld first_token_ms=%lld generation_ms=%lld total_ms=%lld json_early_stop=%d",
            output.size(),
            state->last_output_tokens,
            static_cast<long long>(state->last_prompt_eval_ms),
            static_cast<long long>(state->last_first_token_ms),
            static_cast<long long>(state->last_generation_ms),
            static_cast<long long>(state->last_total_ms),
            state->last_json_early_stop ? 1 : 0);

        if (output.empty()) {
            return env->NewStringUTF("NATIVE_ERROR: empty_generation_no_tokens");
        }

        return env->NewStringUTF(output.c_str());
    } catch (const std::exception& exc) {
        std::string error = std::string("NATIVE_ERROR: ") + exc.what();
        return env->NewStringUTF(error.c_str());
    } catch (...) {
        return env->NewStringUTF("NATIVE_ERROR: nativeGenerate failed");
    }
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeGetLastMetrics(
    JNIEnv* env,
    jclass,
    jlong handle) {
    auto* state = reinterpret_cast<LlamaCommandContext*>(handle);
    if (state == nullptr) return env->NewStringUTF("{}");
    std::lock_guard<std::mutex> lock(state->mutex);
    const std::string json = "{\"prompt_tokens\":" + std::to_string(state->last_prompt_tokens)
        + ",\"output_tokens\":" + std::to_string(state->last_output_tokens)
        + ",\"prompt_eval_ms\":" + std::to_string(state->last_prompt_eval_ms)
        + ",\"first_token_ms\":" + std::to_string(state->last_first_token_ms)
        + ",\"generation_ms\":" + std::to_string(state->last_generation_ms)
        + ",\"total_ms\":" + std::to_string(state->last_total_ms)
        + ",\"json_early_stop\":" + (state->last_json_early_stop ? "true" : "false") + "}";
    return env->NewStringUTF(json.c_str());
}

extern "C" JNIEXPORT void JNICALL
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeFree(
    JNIEnv*,
    jclass,
    jlong handle) {
    free_context(reinterpret_cast<LlamaCommandContext*>(handle));
}
