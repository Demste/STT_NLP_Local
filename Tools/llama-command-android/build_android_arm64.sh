#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

if [[ -z "${LLAMA_CPP_DIR:-}" ]]; then
  echo "LLAMA_CPP_DIR is required. Example: LLAMA_CPP_DIR=/absolute/path/to/llama.cpp ./build_android_arm64.sh" >&2
  exit 2
fi

if [[ ! -d "${LLAMA_CPP_DIR}" ]]; then
  echo "LLAMA_CPP_DIR does not exist: ${LLAMA_CPP_DIR}" >&2
  exit 1
fi

if [[ ! -f "${LLAMA_CPP_DIR}/CMakeLists.txt" || ! -f "${LLAMA_CPP_DIR}/include/llama.h" ]]; then
  echo "LLAMA_CPP_DIR does not look like a llama.cpp checkout: ${LLAMA_CPP_DIR}" >&2
  exit 1
fi

NDK_DIR="${ANDROID_NDK_HOME:-${ANDROID_NDK:-}}"
if [[ -z "${NDK_DIR}" ]]; then
  echo "ANDROID_NDK_HOME or ANDROID_NDK is required." >&2
  exit 2
fi

TOOLCHAIN_FILE="${NDK_DIR}/build/cmake/android.toolchain.cmake"
if [[ ! -f "${TOOLCHAIN_FILE}" ]]; then
  echo "Android NDK toolchain file not found: ${TOOLCHAIN_FILE}" >&2
  exit 1
fi

if ! command -v cmake >/dev/null 2>&1; then
  echo "cmake is required but was not found on PATH." >&2
  exit 1
fi

BUILD_DIR="${SCRIPT_DIR}/build/arm64-v8a"
OUTPUT_DIR="${PROJECT_ROOT}/Assets/Plugins/Android/STTBridge.androidlib/src/main/jniLibs/arm64-v8a"
OUTPUT_FILE="${OUTPUT_DIR}/libllama_command.so"

cmake -S "${SCRIPT_DIR}" -B "${BUILD_DIR}" \
  -DCMAKE_TOOLCHAIN_FILE="${TOOLCHAIN_FILE}" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-24 \
  -DCMAKE_BUILD_TYPE=Release \
  -DLLAMA_CPP_DIR="${LLAMA_CPP_DIR}"

cmake --build "${BUILD_DIR}" --config Release --target llama_command -j"$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

mkdir -p "${OUTPUT_DIR}"
cp "${BUILD_DIR}/libllama_command.so" "${OUTPUT_FILE}"

echo "Copied ${OUTPUT_FILE}"
