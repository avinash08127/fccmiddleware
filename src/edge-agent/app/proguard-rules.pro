# ──────────────────────────────────────────────────────────────────────────────
# kotlinx-serialization
# Keep @Serializable class companion objects so the serializer lookup works.
# ──────────────────────────────────────────────────────────────────────────────
-keepattributes RuntimeVisibleAnnotations,AnnotationDefault

-if @kotlinx.serialization.Serializable class **
-keepclassmembers class <1> {
    static <1>$Companion Companion;
}

-if @kotlinx.serialization.Serializable class ** {
    static **$* *;
}
-keepclassmembers class <2>$<3> {
    kotlinx.serialization.KSerializer serializer(...);
}

-if @kotlinx.serialization.Serializable class ** {
    public static ** INSTANCE;
}
-keepclassmembers class <1> {
    public static <1> INSTANCE;
    kotlinx.serialization.KSerializer serializer(...);
}

# Keep generated $$serializer classes for all app DTOs.
-keep,includedescriptorclasses class com.fccmiddleware.edge.**$$serializer { *; }

# ──────────────────────────────────────────────────────────────────────────────
# Ktor (embedded server + client)
# Ktor uses reflection heavily for routing and content-negotiation.
# ──────────────────────────────────────────────────────────────────────────────
-keep class io.ktor.** { *; }
-keep class kotlinx.coroutines.** { *; }
-dontwarn io.ktor.**
-dontwarn kotlinx.coroutines.**

# ──────────────────────────────────────────────────────────────────────────────
# Room (SQLite ORM)
# ──────────────────────────────────────────────────────────────────────────────
-keep class * extends androidx.room.RoomDatabase
-keep @androidx.room.Entity class *
-keep @androidx.room.Dao interface *
-dontwarn androidx.room.paging.**

# ──────────────────────────────────────────────────────────────────────────────
# Koin (DI)
# ──────────────────────────────────────────────────────────────────────────────
-keep class org.koin.** { *; }
-dontwarn org.koin.**
