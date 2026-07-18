cmake_minimum_required(VERSION 3.22)
if(POLICY CMP0069)
    cmake_policy(SET CMP0069 NEW)
endif()
project(stt_whisper_android LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT DEFINED WHISPER_CPP_DIR)
    message(FATAL_ERROR "Set -DWHISPER_CPP_DIR=/absolute/path/to/whisper.cpp")
endif()

if(NOT EXISTS "${WHISPER_CPP_DIR}/CMakeLists.txt" OR
   NOT EXISTS "${WHISPER_CPP_DIR}/include/whisper.h")
    message(FATAL_ERROR "WHISPER_CPP_DIR is not a whisper.cpp checkout: ${WHISPER_CPP_DIR}")
endif()

if(NOT ANDROID_ABI STREQUAL "arm64-v8a")
    message(FATAL_ERROR "This target is intentionally limited to arm64-v8a; got: ${ANDROID_ABI}")
endif()

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(WHISPER_COREML OFF CACHE BOOL "" FORCE)
set(WHISPER_OPENVINO OFF CACHE BOOL "" FORCE)

set(GGML_CPU ON CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_BLAS OFF CACHE BOOL "" FORCE)
set(GGML_CUDA OFF CACHE BOOL "" FORCE)
set(GGML_METAL OFF CACHE BOOL "" FORCE)
set(GGML_VULKAN OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)

# Android arm64-v8a guarantees ASIMD/NEON and ARM FMA. Keep the generic
# ARMv8-A baseline instead of selecting one phone-specific -mcpu value.
set(GGML_CPU_ARM_ARCH "armv8-a" CACHE STRING "" FORCE)

# whisper.cpp currently declares an older CMake policy baseline in its
# subproject. Keep supported IPO active for targets created there as well.
set(CMAKE_POLICY_DEFAULT_CMP0069 NEW)
add_subdirectory("${WHISPER_CPP_DIR}" whisper.cpp-build)

add_library(stt_whisper SHARED whisper_jni.cpp)

target_include_directories(stt_whisper PRIVATE
    "${WHISPER_CPP_DIR}/include"
    "${WHISPER_CPP_DIR}/ggml/include"
)

target_link_libraries(stt_whisper PRIVATE
    whisper
    android
    log
)

set(STT_RELEASE_OPTIONS
    -O3
    -DNDEBUG
    -ffast-math
    -fno-finite-math-only
    -fdata-sections
    -ffunction-sections
    -march=armv8-a
)

foreach(target_name IN ITEMS stt_whisper whisper ggml ggml-base ggml-cpu)
    if(TARGET ${target_name})
        target_compile_options(${target_name} PRIVATE ${STT_RELEASE_OPTIONS})
    endif()
endforeach()

target_link_options(stt_whisper PRIVATE
    -Wl,--gc-sections
    -Wl,--exclude-libs,ALL
)

set_target_properties(stt_whisper PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
)

include(CheckIPOSupported)
check_ipo_supported(RESULT ipo_supported OUTPUT ipo_error)
if(ipo_supported)
    foreach(target_name IN ITEMS stt_whisper whisper ggml ggml-base ggml-cpu)
        if(TARGET ${target_name})
            set_property(TARGET ${target_name} PROPERTY INTERPROCEDURAL_OPTIMIZATION TRUE)
        endif()
    endforeach()
    message(STATUS "IPO/LTO enabled for stt_whisper and whisper.cpp CPU targets")
else()
    message(WARNING "IPO/LTO unavailable: ${ipo_error}")
endif()
