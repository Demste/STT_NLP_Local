#include <jni.h>

#include <android/log.h>
#include <whisper.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iomanip>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

namespace {

constexpr const char * kLogTag = "STTWhisper";
constexpr int kRequiredSampleRate = 16000;
constexpr int kDefaultThreadCount = 4;
constexpr int kMinThreadCount = 1;
constexpr int kMaxThreadCount = 6;
constexpr const char * kTurkishDroneInitialPrompt =
        u8"Kartal drone komutları: havalan, kalk, uç, in, kalktığın yere in, eve dön, "
        u8"bulunduğun yere in, mevcut konuma in, bekleme noktasına in, "
        u8"ileri git, geri git, sağa git, sola git, yüksel, yukarı çık, aşağı in, alçal, "
        u8"hızını ayarla, kilometre saat, derece dön, saat yönünde dön, saat yönünün tersine dön, "
        u8"tırı bul, kamyonu bul, arabayı bul, ekskavatörü bul, yükleyiciyi bul, forklifti bul, "
        u8"beton mikserini bul, vinç bul, takip et, izle, üzerinde dur, hover yap, "
        u8"gözlem yap, konuma git, marker bul, kırmızı çizgiyi bul, yeşil kareyi bul, "
        u8"mavi üçgeni bul, beyaz artı işaretini bul, dur, bekle, "
        u8"sabit kal, hareket etme.";

using Clock = std::chrono::steady_clock;

struct WavMetadata {
    uint16_t audio_format = 0;
    uint16_t channels = 0;
    uint32_t sample_rate = 0;
    uint16_t bits_per_sample = 0;
    uint32_t data_size = 0;
    std::streampos data_offset{};
};

struct NativeTiming {
    double audio_prepare_ms = 0.0;
    double whisper_full_ms = 0.0;
    double result_read_ms = 0.0;
    double jni_total_ms = 0.0;
    double audio_duration_sec = 0.0;
};

struct WhisperSession {
    explicit WhisperSession(whisper_context * value, int threads)
        : context(value), thread_count(threads) {}

    ~WhisperSession() {
        if (context != nullptr) {
            whisper_free(context);
            context = nullptr;
        }
    }

    whisper_context * context = nullptr;
    int thread_count = kDefaultThreadCount;
    std::mutex inference_mutex;
    std::vector<float> pcm_buffer;
};

std::mutex g_registry_mutex;
std::unordered_map<jlong, std::shared_ptr<WhisperSession>> g_sessions;
std::atomic<jlong> g_next_handle{1};
std::once_flag g_log_config_once;

double elapsed_ms(const Clock::time_point & start) {
    return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
}

uint16_t read_u16_le(const unsigned char * value) {
    return static_cast<uint16_t>(value[0]) |
           static_cast<uint16_t>(value[1] << 8U);
}

uint32_t read_u32_le(const unsigned char * value) {
    return static_cast<uint32_t>(value[0]) |
           (static_cast<uint32_t>(value[1]) << 8U) |
           (static_cast<uint32_t>(value[2]) << 16U) |
           (static_cast<uint32_t>(value[3]) << 24U);
}

bool read_exact(std::istream & input, void * destination, std::streamsize size) {
    input.read(static_cast<char *>(destination), size);
    return input.good() || input.gcount() == size;
}

bool read_wav_metadata(std::ifstream & input, WavMetadata & metadata, std::string & error) {
    std::array<unsigned char, 12> riff{};
    if (!read_exact(input, riff.data(), static_cast<std::streamsize>(riff.size())) ||
        std::memcmp(riff.data(), "RIFF", 4) != 0 ||
        std::memcmp(riff.data() + 8, "WAVE", 4) != 0) {
        error = "Audio file is not a RIFF/WAVE file.";
        return false;
    }

    bool found_format = false;
    bool found_data = false;
    while (input && !found_data) {
        std::array<unsigned char, 8> chunk_header{};
        if (!read_exact(input, chunk_header.data(), static_cast<std::streamsize>(chunk_header.size()))) {
            break;
        }

        const uint32_t chunk_size = read_u32_le(chunk_header.data() + 4);
        if (std::memcmp(chunk_header.data(), "fmt ", 4) == 0) {
            if (chunk_size < 16) {
                error = "WAV fmt chunk is too small.";
                return false;
            }

            std::array<unsigned char, 16> format{};
            if (!read_exact(input, format.data(), static_cast<std::streamsize>(format.size()))) {
                error = "WAV fmt chunk is truncated.";
                return false;
            }

            metadata.audio_format = read_u16_le(format.data());
            metadata.channels = read_u16_le(format.data() + 2);
            metadata.sample_rate = read_u32_le(format.data() + 4);
            metadata.bits_per_sample = read_u16_le(format.data() + 14);
            if (chunk_size > format.size()) {
                input.seekg(static_cast<std::streamoff>(chunk_size - format.size()), std::ios::cur);
            }
            found_format = true;
        } else if (std::memcmp(chunk_header.data(), "data", 4) == 0) {
            metadata.data_size = chunk_size;
            metadata.data_offset = input.tellg();
            found_data = true;
        } else {
            input.seekg(static_cast<std::streamoff>(chunk_size), std::ios::cur);
        }

        if (!found_data && (chunk_size & 1U) != 0U) {
            input.seekg(1, std::ios::cur);
        }
    }

    if (!found_format || !found_data) {
        error = "WAV is missing fmt or data chunk.";
        return false;
    }
    if (metadata.audio_format != 1) {
        error = "Only PCM WAV input is supported.";
        return false;
    }
    if (metadata.channels != 1 || metadata.sample_rate != kRequiredSampleRate || metadata.bits_per_sample != 16) {
        std::ostringstream message;
        message << "Expected mono 16 kHz PCM16 WAV; got channels=" << metadata.channels
                << " sampleRate=" << metadata.sample_rate
                << " bits=" << metadata.bits_per_sample << '.';
        error = message.str();
        return false;
    }
    if (metadata.data_size == 0 || (metadata.data_size % 2U) != 0U) {
        error = "WAV data chunk is empty or has an invalid PCM16 size.";
        return false;
    }
    return true;
}

bool prepare_audio(const std::string & wav_path,
                   std::vector<float> & pcm,
                   double & audio_duration_sec,
                   std::string & error) {
    std::ifstream input(wav_path, std::ios::binary);
    if (!input.is_open()) {
        error = "Unable to open WAV file: " + wav_path;
        return false;
    }

    WavMetadata metadata;
    if (!read_wav_metadata(input, metadata, error)) {
        return false;
    }

    input.clear();
    input.seekg(metadata.data_offset);
    const size_t sample_count = metadata.data_size / sizeof(int16_t);
    pcm.resize(sample_count);

    std::array<unsigned char, 8192> bytes{};
    size_t output_index = 0;
    uint32_t remaining = metadata.data_size;
    while (remaining > 0) {
        const size_t to_read = std::min<size_t>(bytes.size(), remaining);
        if (!read_exact(input, bytes.data(), static_cast<std::streamsize>(to_read))) {
            error = "WAV data chunk is truncated.";
            return false;
        }
        for (size_t offset = 0; offset < to_read; offset += 2) {
            const uint16_t raw = read_u16_le(bytes.data() + offset);
            const int16_t sample = static_cast<int16_t>(raw);
            pcm[output_index++] = static_cast<float>(sample) / 32768.0F;
        }
        remaining -= static_cast<uint32_t>(to_read);
    }

    audio_duration_sec = static_cast<double>(sample_count) / kRequiredSampleRate;
    return true;
}

std::string escape_json(const std::string & value) {
    std::ostringstream escaped;
    for (const unsigned char ch : value) {
        switch (ch) {
            case '\"': escaped << "\\\""; break;
            case '\\': escaped << "\\\\"; break;
            case '\b': escaped << "\\b"; break;
            case '\f': escaped << "\\f"; break;
            case '\n': escaped << "\\n"; break;
            case '\r': escaped << "\\r"; break;
            case '\t': escaped << "\\t"; break;
            default:
                if (ch < 0x20U) {
                    escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                            << static_cast<int>(ch) << std::dec;
                } else {
                    escaped << static_cast<char>(ch);
                }
        }
    }
    return escaped.str();
}

std::string build_result_json(bool ok,
                              const std::string & text,
                              const std::string & error,
                              const NativeTiming & timing) {
    std::ostringstream json;
    json << std::fixed << std::setprecision(3)
         << "{\"ok\":" << (ok ? "true" : "false")
         << ",\"text\":\"" << escape_json(text) << '"'
         << ",\"error\":\"" << escape_json(error) << '"'
         << ",\"audioPrepareMs\":" << timing.audio_prepare_ms
         << ",\"whisperFullMs\":" << timing.whisper_full_ms
         << ",\"resultReadMs\":" << timing.result_read_ms
         << ",\"jniTotalMs\":" << timing.jni_total_ms
         << ",\"audioDurationSec\":" << timing.audio_duration_sec
         << '}';
    return json.str();
}

void quiet_whisper_log(enum ggml_log_level level, const char * text, void *) {
    if (level == GGML_LOG_LEVEL_ERROR && text != nullptr) {
        __android_log_print(ANDROID_LOG_ERROR, kLogTag, "%s", text);
    }
}

void throw_java(JNIEnv * env, const char * class_name, const std::string & message) {
    jclass exception_class = env->FindClass(class_name);
    if (exception_class != nullptr) {
        env->ThrowNew(exception_class, message.c_str());
        env->DeleteLocalRef(exception_class);
    }
}

bool get_utf8(JNIEnv * env, jstring value, const char * field_name, std::string & output) {
    if (value == nullptr) {
        throw_java(env, "java/lang/IllegalArgumentException", std::string(field_name) + " is null.");
        return false;
    }
    const char * chars = env->GetStringUTFChars(value, nullptr);
    if (chars == nullptr || env->ExceptionCheck()) {
        return false;
    }
    output.assign(chars);
    env->ReleaseStringUTFChars(value, chars);
    return true;
}

std::shared_ptr<WhisperSession> find_session(jlong handle) {
    std::lock_guard<std::mutex> lock(g_registry_mutex);
    const auto found = g_sessions.find(handle);
    return found == g_sessions.end() ? nullptr : found->second;
}

}  // namespace

extern "C" JNIEXPORT jlong JNICALL
Java_com_mehmet_sttbridge_STTBridge_whisperInitNative(
        JNIEnv * env,
        jclass,
        jstring model_path_value,
        jint requested_thread_count) {
    std::string model_path;
    if (!get_utf8(env, model_path_value, "modelPath", model_path)) {
        return 0;
    }
    if (model_path.empty()) {
        throw_java(env, "java/lang/IllegalArgumentException", "modelPath is empty.");
        return 0;
    }

    const int thread_count = std::clamp(
            static_cast<int>(requested_thread_count), kMinThreadCount, kMaxThreadCount);
    std::call_once(g_log_config_once, []() { whisper_log_set(quiet_whisper_log, nullptr); });

    whisper_context_params context_params = whisper_context_default_params();
    context_params.use_gpu = false;
    context_params.flash_attn = false;
    whisper_context * context = whisper_init_from_file_with_params(model_path.c_str(), context_params);
    if (context == nullptr) {
        throw_java(env, "java/lang/IllegalStateException", "whisper.cpp model initialization failed: " + model_path);
        return 0;
    }

    const auto session = std::make_shared<WhisperSession>(context, thread_count);
    jlong handle = g_next_handle.fetch_add(1);
    if (handle <= 0) {
        handle = g_next_handle.fetch_add(1);
    }
    {
        std::lock_guard<std::mutex> lock(g_registry_mutex);
        g_sessions.emplace(handle, session);
    }
    return handle;
}

extern "C" JNIEXPORT jstring JNICALL
Java_com_mehmet_sttbridge_STTBridge_whisperTranscribeNative(
        JNIEnv * env,
        jclass,
        jlong handle,
        jstring wav_path_value,
        jstring language_value) {
    const auto total_started = Clock::now();
    NativeTiming timing;

    const auto session = find_session(handle);
    if (session == nullptr) {
        timing.jni_total_ms = elapsed_ms(total_started);
        const std::string json = build_result_json(false, "", "Whisper context handle is invalid or already freed.", timing);
        return env->NewStringUTF(json.c_str());
    }

    std::string wav_path;
    std::string language;
    if (!get_utf8(env, wav_path_value, "wavPath", wav_path) ||
        !get_utf8(env, language_value, "language", language)) {
        return nullptr;
    }
    if (language.empty()) {
        language = "tr";
    }

    std::lock_guard<std::mutex> inference_lock(session->inference_mutex);
    std::string error;

    const auto audio_started = Clock::now();
    const bool audio_ready = prepare_audio(
            wav_path, session->pcm_buffer, timing.audio_duration_sec, error);
    timing.audio_prepare_ms = elapsed_ms(audio_started);
    if (!audio_ready) {
        timing.jni_total_ms = elapsed_ms(total_started);
        const std::string json = build_result_json(false, "", error, timing);
        return env->NewStringUTF(json.c_str());
    }

    whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.language = language.c_str();
    params.detect_language = false;
    params.translate = false;
    params.no_context = true;
    params.no_timestamps = true;
    params.single_segment = true;
    params.max_len = 0;
    params.max_tokens = 0;
    params.initial_prompt = kTurkishDroneInitialPrompt;
    params.carry_initial_prompt = false;
    params.temperature = 0.0F;
    params.temperature_inc = 0.0F;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.print_special = false;
    params.n_threads = session->thread_count;

    __android_log_print(
            ANDROID_LOG_INFO,
            kLogTag,
            "[WhisperConfig] language=%s translate=false temperature=0.0 noTimestamps=true "
            "singleSegment=true maxLen=0 maxTokens=0 initialPrompt=turkish_drone_commands_v1 threads=%d",
            language.c_str(),
            session->thread_count);

    const auto inference_started = Clock::now();
    const int inference_status = whisper_full(
            session->context,
            params,
            session->pcm_buffer.data(),
            static_cast<int>(session->pcm_buffer.size()));
    timing.whisper_full_ms = elapsed_ms(inference_started);
    if (inference_status != 0) {
        timing.jni_total_ms = elapsed_ms(total_started);
        const std::string json = build_result_json(false, "", "whisper_full failed with status " +
                std::to_string(inference_status) + '.', timing);
        return env->NewStringUTF(json.c_str());
    }

    const auto result_started = Clock::now();
    std::string transcript;
    const int segment_count = whisper_full_n_segments(session->context);
    for (int segment = 0; segment < segment_count; ++segment) {
        const char * segment_text = whisper_full_get_segment_text(session->context, segment);
        if (segment_text != nullptr) {
            transcript.append(segment_text);
        }
    }
    timing.result_read_ms = elapsed_ms(result_started);
    timing.jni_total_ms = elapsed_ms(total_started);

    const std::string json = build_result_json(true, transcript, "", timing);
    return env->NewStringUTF(json.c_str());
}

extern "C" JNIEXPORT void JNICALL
Java_com_mehmet_sttbridge_STTBridge_whisperFreeNative(
        JNIEnv *,
        jclass,
        jlong handle) {
    if (handle <= 0) {
        return;
    }

    std::shared_ptr<WhisperSession> released;
    {
        std::lock_guard<std::mutex> lock(g_registry_mutex);
        const auto found = g_sessions.find(handle);
        if (found == g_sessions.end()) {
            return;
        }
        released = found->second;
        g_sessions.erase(found);
    }
    // The context is freed exactly once when this reference and any in-flight
    // transcription reference have both gone out of scope.
}
