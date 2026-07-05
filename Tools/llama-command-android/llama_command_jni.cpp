#include <jni.h>

#include <android/log.h>

#include <algorithm>
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
    std::mutex mutex;
};

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
    jint top_k) {
    auto* state = reinterpret_cast<LlamaCommandContext*>(handle);
    if (state == nullptr || state->ctx == nullptr || state->model == nullptr) {
        return env->NewStringUTF("NATIVE_ERROR: nativeGenerate called with invalid handle");
    }

    std::lock_guard<std::mutex> lock(state->mutex);

    try {
        std::string prompt = jstring_to_utf8(env, prompt_value);
        std::vector<llama_token> tokens = tokenize(state, prompt);
        const int limit = std::max<int>(1, max_tokens);
        const int safety_margin = 32;
        const int prompt_tokens = static_cast<int>(tokens.size());
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

        llama_batch batch = llama_batch_get_one(tokens.data(), static_cast<int32_t>(tokens.size()));
        if (llama_decode(state->ctx, batch) != 0) {
            return env->NewStringUTF("NATIVE_ERROR: llama_decode_failed_for_prompt");
        }

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

        if (temp <= 0.0f) {
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_greedy());
        } else {
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_k(k));
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_p(nucleus, 1));
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_temp(temp));
            llama_sampler_chain_add(sampler.get(), llama_sampler_init_dist(LLAMA_DEFAULT_SEED));
        }

        for (int i = 0; i < limit; ++i) {
            llama_token next = llama_sampler_sample(sampler.get(), state->ctx, -1);
            llama_sampler_accept(sampler.get(), next);

            if (i == 0) {
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

            llama_token next_tokens[] = {next};
            llama_batch next_batch = llama_batch_get_one(next_tokens, 1);
            if (llama_decode(state->ctx, next_batch) != 0) {
                return env->NewStringUTF("NATIVE_ERROR: llama_decode_failed_during_generation");
            }
        }

        __android_log_print(
            ANDROID_LOG_INFO,
            kLogTag,
            "nativeGenerate output_chars=%zu",
            output.size());

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

extern "C" JNIEXPORT void JNICALL
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeFree(
    JNIEnv*,
    jclass,
    jlong handle) {
    free_context(reinterpret_cast<LlamaCommandContext*>(handle));
}
