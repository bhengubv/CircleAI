plugins {
    kotlin("jvm") version "2.0.21"
    kotlin("plugin.serialization") version "2.0.21"
    `maven-publish`
}

group = "com.bhengubv"
version = project.findProperty("version")?.takeIf { it != "unspecified" } ?: "1.5.0"

repositories {
    mavenCentral()
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.9.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    testImplementation(kotlin("test"))
    testImplementation("org.junit.jupiter:junit-jupiter-params:5.11.0")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.9.0")
}

tasks.test {
    useJUnitPlatform()
    testLogging {
        events("passed", "skipped", "failed")
    }
}

kotlin {
    jvmToolchain(19)
}

publishing {
    publications {
        create<MavenPublication>("mavenKotlin") {
            groupId    = "com.bhengubv"
            artifactId = "circle-ai"
            version    = project.version.toString()
            from(components["java"])
            pom {
                name.set("Circle AI — Kotlin")
                description.set("Circle AI portable core — Kotlin/JVM")
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
