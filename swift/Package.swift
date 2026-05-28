// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CircleAI",
    platforms: [
        .macOS(.v13),
        .iOS(.v16),
        .watchOS(.v9)
    ],
    products: [
        .library(name: "CircleAI", targets: ["CircleAI"])
    ],
    targets: [
        .target(
            name: "CircleAI",
            path: "Sources/CircleAI"
        ),
        .testTarget(
            name: "CircleAITests",
            dependencies: ["CircleAI"],
            path: "Tests/CircleAITests"
        )
    ]
)
