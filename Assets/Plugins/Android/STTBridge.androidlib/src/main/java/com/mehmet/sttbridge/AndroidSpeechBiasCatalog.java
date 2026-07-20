package com.mehmet.sttbridge;

import java.util.ArrayList;

/**
 * Central Turkish drone vocabulary passed to API 33+ recognition services as a hint.
 * These strings mirror the existing canonical action/alias vocabulary. They are never
 * treated as command evidence; Unity's existing evidence gate remains authoritative.
 */
public final class AndroidSpeechBiasCatalog {
    private static final String[] PHRASES = {
            "Kartal",
            "havalan", "kalk", "uç",
            "in", "eve dön", "geri dön", "kalktığın yere in", "başlangıç noktasına dön",
            "bulunduğun yere in", "olduğun yere in", "mevcut konuma in", "bekleme noktasına in",
            "sağa git", "sola git", "ileri git", "geri git", "sağa geç", "sola geç",
            "sağa ilerle", "sola ilerle",
            "yüksel", "yukarı çık", "yukarı çek", "alçal", "aşağı in",
            "hız", "kilometre saat", "kilometre bölü saat",
            "sağa dön", "sola dön", "saat yönünde", "saat yönünün tersine", "derece",
            "kamyon", "tır", "pikap", "araba", "otomobil", "ekskavatör", "yükleyici",
            "forklift", "beton mikseri", "vinç", "bul", "ara", "tespit et", "takip et",
            "izle", "peşinden git", "üzerinde dur", "üstünde bekle",
            "olduğun yerde dur ve izle", "mevcut konumda gözlem yap", "burada bekle ve izle",
            "havada dur", "havada bekle", "hover yap", "noktasına git", "konumuna git",
            "kırmızı", "yeşil", "mavi", "beyaz", "siyah", "çizgi", "kare", "üçgen", "artı işareti",
            "metre", "derece", "saniye", "dakika", "kilometre saat",
            "sıfır", "bir", "iki", "üç", "dört", "beş", "altı", "yedi", "sekiz", "dokuz",
            "on", "yirmi", "otuz", "kırk", "elli", "altmış", "yetmiş", "seksen", "doksan",
            "yüz", "bin"
    };

    private AndroidSpeechBiasCatalog() {}

    public static ArrayList<String> phrases() {
        ArrayList<String> result = new ArrayList<>(PHRASES.length);
        for (String phrase : PHRASES) result.add(phrase);
        return result;
    }
}
