#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
EXPECTED_WHISPER_COMMIT="f049fff95a089aa9969deb009cdd4892b3e74916"

WHISPER_CPP_DIR="${WHISPER_CPP_DIR:-${PROJECT_ROOT}/Tools/third_party/whisper.cpp}"
if [[ ! -f "${WHISPER_CPP_DIR}/CMakeLists.txt" || ! -f "${WHISPER_CPP_DIR}/include/whisper.h" ]]; then
  echo "WHISPER_CPP_DIR does not look like a whisper.cpp checkout: ${WHISPER_CPP_DIR}" >&2
  exit 1
fi

if command -v git >/dev/null 2>&1 && [[ -d "${WHISPER_CPP_DIR}/.git" ]]; then
  ACTUAL_WHISPER_COMMIT="$(git -C "${WHISPER_CPP_DIR}" rev-parse HEAD)"
  if [[ "${ACTUAL_WHISPER_COMMIT}" != "${EXPECTED_WHISPER_COMMIT}" ]]; then
    echo "Expected whisper.cpp ${EXPECTED_WHISPER_COMMIT}, got ${ACTUAL_WHISPER_COMMIT}." >&2
    exit 1
  fi
fi

NDK_DIR="${ANDROID_NDK_HOME:-${ANDROID_NDK:-}}"
if [[ -z "${NDK_DIR}" ]]; then
  echo "ANDROID_NDK_HOME or ANDROID_NDK is required." >&2
  exit 2
fi

TOOLCHAIN_FILE="${NDK_DIR}/build/cmake/android.toolchain.cmake"
LLVM_STRIP="${NDK_DIR}/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip"
if [[ ! -f "${TOOLCHAIN_FILE}" || ! -x "${LLVM_STRIP}" ]]; then
  echo "Android NDK toolchain or llvm-strip not found under: ${NDK_DIR}" >&2
  exit 1
fi

BUILD_DIR="${SCRIPT_DIR}/build/arm64-v8a"
OUTPUT_DIR="${PROJECT_ROOT}/Assets/Plugins/Android/STTBridge.androidlib/src/main/jniLibs/arm64-v8a"
OUTPUT_FILE="${OUTPUT_DIR}/libstt_whisper.so"
BACKUP_FILE="${OUTPUT_FILE}.backup"

cmake -S "${SCRIPT_DIR}" -B "${BUILD_DIR}" \
  -DCMAKE_TOOLCHAIN_FILE="${TOOLCHAIN_FILE}" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-24 \
  -DANDROID_STL=c++_shared \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_EXPORT_COMPILE_COMMANDS=ON \
  -DWHISPER_CPP_DIR="${WHISPER_CPP_DIR}"

cmake --build "${BUILD_DIR}" --config Release --target stt_whisper \
  -j"$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

mkdir -p "${OUTPUT_DIR}"
if [[ -f "${OUTPUT_FILE}" && ! -f "${BACKUP_FILE}" ]]; then
  cp "${OUTPUT_FILE}" "${BACKUP_FILE}"
fi

cp "${BUILD_DIR}/libstt_whisper.so" "${OUTPUT_FILE}"
"${LLVM_STRIP}" --strip-unneeded "${OUTPUT_FILE}"

file "${OUTPUT_FILE}"
ls -lh "${OUTPUT_FILE}"
shasum -a 256 "${OUTPUT_FILE}"
echo "Whisper source commit: ${EXPECTED_WHISPER_COMMIT} (v1.9.1)"
echo "Copied: ${OUTPUT_FILE}"
echo "Original backup: ${BACKUP_FILE}"
