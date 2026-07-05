#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
TARGET_DIR="${PROJECT_ROOT}/Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/llm"
TARGET_FILE="${TARGET_DIR}/command-model.gguf"

if [[ $# -ne 1 ]]; then
  echo "Usage: ./install_model_asset.sh /absolute/path/to/model.gguf" >&2
  exit 2
fi

SOURCE_FILE="$1"

if [[ "${SOURCE_FILE}" != /* ]]; then
  echo "Model path must be absolute: ${SOURCE_FILE}" >&2
  exit 2
fi

if [[ ! -f "${SOURCE_FILE}" ]]; then
  echo "Model file does not exist: ${SOURCE_FILE}" >&2
  exit 1
fi

case "${SOURCE_FILE}" in
  *.gguf) ;;
  *)
    echo "Model file must have .gguf extension: ${SOURCE_FILE}" >&2
    exit 1
    ;;
esac

mkdir -p "${TARGET_DIR}"
cp "${SOURCE_FILE}" "${TARGET_FILE}"

SIZE_BYTES="$(wc -c < "${TARGET_FILE}" | tr -d '[:space:]')"

echo "Installed model:"
echo "${TARGET_FILE}"
echo "File size bytes: ${SIZE_BYTES}"
echo
git -C "${PROJECT_ROOT}" check-attr filter -- "${TARGET_FILE}" || true
echo
echo "Reminder:"
echo "  git lfs install"
echo "  git add .gitattributes ${TARGET_FILE}"
