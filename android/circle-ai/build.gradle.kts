plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("maven-publish")
}

group   = "com.bhengubv"
version = project.findProperty("version")?.takeIf { it != "unspecified" } ?: "0.1.0"

android {
    namespace = "com.bhengubv.circleai"
    compileSdk = 35

    defaultConfig {
        minSdk = 24
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_19
        targetCompatibility = JavaVersion.VERSION_19
    }

    kotlinOptions {
        jvmTarget = "19"
    }

    publishing {
        singleVariant("release") {
            withSourcesJar()
        }
    }
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.9.0")
    testImplementation("com.fasterxml.jackson.module:jackson-module-kotlin:2.17.0")
    testImplementation("com.fasterxml.jackson.core:jackson-databind:2.17.0")
}

afterEvaluate {
    publishing {
        publications {
            create<MavenPublication>("release") {
                from(components["release"])
                groupId    = "com.bhengubv"
                artifactId = "circle-ai-android"
                version    = project.version.toString()
                pom {
                    name.set("Circle AI — Android")
                    description.set("Circle AI portable core — Android/Kotlin")
                    url.set("https://github.com/bhengubv/CircleAI")
                    licenses {
                        license {
                            name.set("MIT")
                            url.set("https://opensource.org/licenses/MIT")
                        }
                    }
                }
            }
        }
        repositories {
            maven {
                name = "GitHubPackages"
                url  = uri("https://maven.pkg.github.com/bhengubv/CircleAI")
                credentials {
                    username = System.getenv("GITHUB_ACTOR") ?: "bhengubv"
                    password = System.getenv("GITHUB_TOKEN") ?: ""
                }
            }
        }
    }
}
