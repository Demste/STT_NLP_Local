# llama-command Android backend

Bu klasör, Unity Android plugin için kullanılan offline LLM backend'inin native JNI adapter'ını build etmek için vardır.

Bu workspace `Assets/Plugins/Android/STTBridge.androidlib` dışında durur. Böylece mevcut Whisper/STT Android plugin yapısı bozulmaz ve Unity/Gradle tarafına yeni bir CMake entegrasyonu eklenmez.

Build varsayılan olarak:

- CPU-only
- Sadece `arm64-v8a`
- Vulkan yok
- GPU yok
- OpenMP yok

şeklindedir.

---

## Native Adapter Source

Native adapter kaynak dosyası:

```text
Tools/llama-command-android/llama_command_jni.cpp
```

Bu dosya C++ kaynak kodudur.

Build edildiğinde şu native library üretilir:

```text
libllama_command.so
```

Bu dosya model değildir.

Bu klasör model klasörü değildir. Bu yüzden `.gguf` model dosyası `Tools/llama-command-android` içine koyulmamalıdır.

---

## Model Asset

Model dosyası `Tools` klasöründe durmaz.

Beklenen Unity Android asset path:

```text
Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/llm/command-model.gguf
```

Final APK/AAB build sırasında bu asset paketlenir.

Runtime'da Java tarafı bu modeli Android asset içinden app-private storage alanına çıkarır. Sonra çıkarılan gerçek filesystem path `nativeInit` metoduna verilir.

Önemli:

`nativeInit` Android asset path almaz.

Yanlış örnek:

```text
assets/llm/command-model.gguf
```

Doğru mantık:

```text
/data/user/0/<package-name>/files/llm/command-model.gguf
```

Yani native taraf gerçek dosya yolu ister.

---

## Model Kurulumu

İndirilmiş `.gguf` model dosyasını doğru Unity Android asset klasörüne kopyalamak için:

```sh
cd Tools/llama-command-android
./install_model_asset.sh /absolute/path/to/downloaded-model.gguf
```

Script modeli şuraya kopyalar:

```text
../../Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/llm/command-model.gguf
```

Bu script:

- Model indirmez.
- Placeholder model oluşturmaz.
- Modeli `Tools` klasörüne kopyalamaz.
- Sadece verilen `.gguf` dosyasını doğru Unity Android asset klasörüne kopyalar.

---

## Native Library Build

Gereksinimler:

- Android NDK
- `ANDROID_NDK_HOME` veya `ANDROID_NDK` environment variable
- CMake
- Lokal bir `llama.cpp` checkout

Build komutu:

```sh
cd Tools/llama-command-android
LLAMA_CPP_DIR=/absolute/path/to/llama.cpp ANDROID_NDK_HOME=/absolute/path/to/ndk ./build_android_arm64.sh
```

Final beklenen çıktı:

```text
Assets/Plugins/Android/STTBridge.androidlib/src/main/jniLibs/arm64-v8a/libllama_command.so
```

Eğer `llama.cpp` proje içindeki `third_party` klasöründe duruyorsa örnek kullanım:

```sh
cd Tools/llama-command-android
LLAMA_CPP_DIR="$(pwd)/third_party/llama.cpp" ANDROID_NDK_HOME=/absolute/path/to/ndk ./build_android_arm64.sh
```

---

## Dosya ve Klasörlerin Görevi

```text
Tools/llama-command-android/
```

Native LLM backend için build alanıdır. Telefon runtime'da bu klasörü doğrudan kullanmaz.

```text
Tools/llama-command-android/llama_command_jni.cpp
```

Java ile `llama.cpp` arasında JNI köprüsüdür.

```text
Tools/llama-command-android/CMakeLists.txt
```

C++ build kurallarını tanımlar.

```text
Tools/llama-command-android/build_android_arm64.sh
```

Android `arm64-v8a` için `libllama_command.so` üretir ve Unity plugin klasörüne kopyalar.

```text
Tools/llama-command-android/install_model_asset.sh
```

İndirilen `.gguf` modeli Unity Android asset klasörüne kopyalar.

```text
Tools/llama-command-android/third_party/llama.cpp
```

Asıl LLM inference motorudur. Modeli yükleme, tokenization ve text üretimi burada yapılır.

```text
Tools/llama-command-android/build/
```

CMake'in geçici build çıktılarıdır. Commit edilmemelidir.

---

## JNI Contract

Java class:

```text
com.mehmet.sttbridge.llm.LlamaCppEngine
```

Native method imzaları:

```java
nativeInit(String modelPath, int contextSize, int threads): long

nativeGenerate(
    long handle,
    String prompt,
    int maxTokens,
    float temperature,
    float topP,
    int topK
): String

nativeFree(long handle): void
```

C++ tarafındaki JNI method isimleri Java package/class adıyla birebir eşleşmelidir:

```cpp
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeInit
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeGenerate
Java_com_mehmet_sttbridge_llm_LlamaCppEngine_nativeFree
```

Java tarafındaki method imzası ile C++ tarafındaki JNI imzası uyuşmazsa Android runtime'da şu tarz hata alınır:

```text
UnsatisfiedLinkError
No implementation found for nativeGenerate(...)
```

---

## nativeInit Parametreleri

```java
nativeInit(String modelPath, int contextSize, int threads)
```

### modelPath

Modelin gerçek filesystem path'idir.

Bu path Android asset path değildir.

Java tarafı önce modeli APK/AAB içindeki asset alanından çıkarır, sonra çıkarılan dosyanın gerçek path'ini native tarafa verir.

### contextSize

Modelin aynı anda kaç tokenlık bağlam penceresi kullanacağını belirler.

Örnek değerler:

```text
1024
2048
4096
```

Daha büyük değer:

- Daha uzun prompt desteği sağlar.
- Daha fazla RAM kullanır.
- Mobilde yavaşlama ve ısınma yapabilir.

Komut anlama için genelde güvenli başlangıç:

```text
1024 veya 2048
```

### threads

Inference sırasında kullanılacak CPU thread sayısıdır.

Örnek değerler:

```text
1
2
4
6
```

Daha yüksek thread sayısı bazı cihazlarda hızlandırabilir ama her zaman daha iyi değildir. CPU ısısı ve pil tüketimi artabilir.

Mobil için başlangıç değeri:

```text
4
```

---

## nativeGenerate Parametreleri

```java
nativeGenerate(
    long handle,
    String prompt,
    int maxTokens,
    float temperature,
    float topP,
    int topK
)
```

### handle

`nativeInit` tarafından dönen native context değeridir.

Basitçe yüklenmiş model oturumunu temsil eder.

Java tarafı bunu saklar ve her generation çağrısında tekrar native tarafa gönderir.

### prompt

Modele verilen input text'tir.

Bu projede prompt genelde kullanıcıdan gelen komutu JSON command formatına çevirmek için kullanılır.

Örnek input:

```text
Kırmızı kutuyu al.
```

Beklenen output örneği:

```json
{"action":"pickup","target":"red_box","confidence":0.85,"reason":"Kullanıcı kırmızı kutuyu almayı istedi."}
```

### maxTokens

Modelin en fazla kaç yeni token üreteceğini belirler.

Komut JSON'u için çok yüksek tutulmasına gerek yoktur.

Örnek değerler:

```text
64
128
192
```

Çok düşük olursa çıktı yarım kalabilir.

Çok yüksek olursa cevap süresi uzar ve model gereksiz uzun çıktı üretebilir.

Komut anlama için önerilen başlangıç:

```text
128
```

### temperature

Modelin ne kadar rastgele veya yaratıcı davranacağını belirler.

Düşük değerler daha stabil ve deterministik çıktı verir.

Örnek:

```text
0.0 - 0.2
```

Yüksek değerler daha yaratıcı ama daha değişken çıktı verir.

Örnek:

```text
0.7 - 1.0
```

Bu projede amaç yaratıcı cevap değil, stabil JSON command üretmek olduğu için önerilen değer:

```text
0.1
```

Eğer `temperature = 0.0` verilirse greedy sampling kullanılır. Bu durumda model genelde en olası tokenları seçer.

### topP

Nucleus sampling değeridir.

Modelin token seçerken olasılık havuzunu ne kadar geniş tutacağını belirler.

Değer aralığı:

```text
0.05 - 1.0
```

Düşük değer daha dar ve güvenli seçim yapar.

Yüksek değer daha geniş token havuzu kullanır.

Komut anlama için önerilen başlangıç:

```text
0.95
```

Not:

`temperature = 0.0` ise `topP` pratikte etkisiz olabilir çünkü greedy sampling kullanılır.

### topK

Modelin her adımda en olası kaç token arasından seçim yapacağını belirler.

Örnek değerler:

```text
1
20
40
64
```

Düşük değer daha deterministik davranır.

Yüksek değer daha geniş seçim alanı verir.

Komut anlama için önerilen başlangıç:

```text
40
```

Not:

`temperature = 0.0` ise `topK` pratikte etkisiz olabilir çünkü greedy sampling kullanılır.

---

## Önerilen LLM Config

Komut anlama ve JSON üretimi için başlangıç config'i:

```json
{
  "llm": {
    "model_asset": "command-model.gguf",
    "context_size": 1024,
    "threads": 4,
    "max_tokens": 128,
    "temperature": 0.1,
    "top_p": 0.95,
    "top_k": 40
  }
}
```

Daha uzun prompt veya daha uzun JSON çıktısı gerekiyorsa:

```json
{
  "llm": {
    "model_asset": "command-model.gguf",
    "context_size": 2048,
    "threads": 4,
    "max_tokens": 192,
    "temperature": 0.1,
    "top_p": 0.95,
    "top_k": 40
  }
}
```

---

## Runtime Akışı

```text
1. Unity C# tarafı kullanıcı text'ini Android Java bridge'e gönderir.
2. Java tarafı LlamaCppEngine'i kullanır.
3. Java, GGUF modeli Android asset içinden app-private storage alanına çıkarır.
4. Java, çıkarılan gerçek model path ile nativeInit çağırır.
5. nativeInit modeli llama.cpp ile yükler ve handle döndürür.
6. Unity/Java tarafı prompt ile nativeGenerate çağırır.
7. C++ tarafı prompt'u tokenlara çevirir.
8. llama.cpp modelden yeni tokenlar üretir.
9. Üretilen tokenlar text'e çevrilir.
10. Text Java'ya, oradan Unity C# tarafına döner.
11. Unity tarafı sonucu JSON command olarak parse eder.
```

---

## Beklenen Dosyalar

Native library:

```text
Assets/Plugins/Android/STTBridge.androidlib/src/main/jniLibs/arm64-v8a/libllama_command.so
```

Model asset:

```text
Assets/Plugins/Android/STTBridge.androidlib/src/main/assets/llm/command-model.gguf
```

---

## Push Öncesi Kontrol

```text
[ ] libllama_command.so doğru jniLibs/arm64-v8a klasöründe mi?
[ ] command-model.gguf doğru assets/llm klasöründe mi?
[ ] GGUF model yanlışlıkla Tools klasörüne konmadı mı?
[ ] build/ klasörü commit'e girmiyor mu?
[ ] Java nativeGenerate imzası C++ imzasıyla aynı mı?
[ ] README'deki JNI Contract güncel mi?
[ ] Model dosyası Git LFS veya Drive ile yönetiliyor mu?
```

Önerilen `.gitignore` kuralı:

```gitignore
Tools/llama-command-android/build/
```

GGUF model büyük olduğu için normal git'e eklemek repo'yu şişirebilir. Gerekirse Git LFS veya Drive kullanılmalıdır.

---

## Kısa Özet

```text
Tools/llama-command-android
    Native backend build alanı.

llama_command_jni.cpp
    Java ile llama.cpp arasında C++ köprü.

third_party/llama.cpp
    Asıl LLM inference motoru.

build_android_arm64.sh
    Android arm64-v8a için libllama_command.so üretir.

install_model_asset.sh
    GGUF modeli Unity Android asset klasörüne kopyalar.

libllama_command.so
    Android cihazda çalışan native LLM backend.

command-model.gguf
    Offline LLM model dosyası.
```