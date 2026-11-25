plugins {
    id("com.android.application")
}

android {
    namespace = "com.openpixel.deck"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.openpixel.deck"
        minSdk = 28
        targetSdk = 35
        versionCode = 1
        versionName = "1.0.5"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
        debug {
            isDebuggable = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
}
