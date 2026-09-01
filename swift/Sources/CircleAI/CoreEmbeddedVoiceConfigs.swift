// CoreEmbeddedVoiceConfigs.swift
//
// The MMS voices' `model.onnx.json` sidecars, carried inside the package.
//
// WHY THEY ARE NOT DOWNLOADED. The registry pinned each sidecar as a remote
// bundle file with a SHA-256, exactly like the 114 MB model beside it. Measured
// 2026-08-23, 43 of the 47 returned 404: they were generated once by a script
// that was never committed and the bytes were then lost, so the registry was
// promising files that no longer existed anywhere. Every one of those voices
// downloaded its model and its tokens.txt and then failed on a 2 KB sidecar.
//
// The whole set is 91 KB — smaller than one app icon, and it is our own work
// product rather than an upstream artefact. Carrying it removes the failure
// permanently: no address to go stale, no credential needed to publish it, and
// nothing extra for anyone to install.
//
// The SHA in the registry still governs. The downloader writes these bytes into
// the bundle directory and then runs the ordinary verify-then-skip path over
// them, so a sidecar that does not match its pin fails exactly the way a corrupt
// download would.
//
// Ported from src/CircleAI.Core/Models/EmbeddedVoiceConfigs.cs.

import Foundation

public enum EmbeddedVoiceConfigs {

    /// The two files a voice can carry. Both are ours; neither is downloadable.
    static let companions = ["model.onnx.json", "language_ids.json"]

    /// Where the sidecars live.
    ///
    /// NOT `Bundle.module`: that symbol exists only when the SwiftPM target
    /// declares resources, and this one deliberately declares none — the C# side
    /// carries these as embedded assembly resources, and there is no equivalent
    /// that works for a host vendoring these sources directly. So the directory
    /// is discovered instead: what a host points at, then the app bundle's own
    /// resources, then a `VoiceConfigs` folder beside the executable.
    nonisolated(unsafe) private static var overrideDirectory: String?

    /// Point at a directory of `<voice>/model.onnx.json` files. Set by a host
    /// that ships the sidecars itself; nil uses the package's own resources.
    public static var resourceDirectory: String? {
        get { lock.lock(); defer { lock.unlock() }; return overrideDirectory }
        set {
            lock.lock()
            overrideDirectory = newValue
            cached = nil                       // the map is derived from it
            lock.unlock()
        }
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var cached: [String: URL]?

    /// Every bundle-relative name that is carried, e.g. "mms-swh/model.onnx.json".
    public static var names: [String] {
        map().keys.sorted()
    }

    /// The bytes for one bundle file, or nil when it is not one of ours.
    ///
    /// Backslashes are folded to forward slashes first: a bundle manifest
    /// written on Windows names the same file a different way, and a lookup that
    /// misses here falls through to a download of a file that does not exist.
    public static func bytes(forBundleFile name: String?) -> Data? {
        guard let name, !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        let key = name.replacingOccurrences(of: "\\", with: "/")
        guard let url = map()[key] else { return nil }
        return try? Data(contentsOf: url)
    }

    /// Which voice ids carry a sidecar at all.
    public static var voices: [String] {
        Set(map().keys.compactMap { $0.split(separator: "/").first.map(String.init) }).sorted()
    }

    private static func map() -> [String: URL] {
        lock.lock()
        if let cached { lock.unlock(); return cached }
        let dir = overrideDirectory
        lock.unlock()

        let built = build(in: dir)

        lock.lock(); cached = built; lock.unlock()
        return built
    }

    private static func build(in override: String?) -> [String: URL] {
        var roots: [URL] = []
        if let override { roots.append(URL(fileURLWithPath: override)) }

        if let resources = Bundle.main.resourceURL {
            roots.append(resources.appendingPathComponent("VoiceConfigs"))
            roots.append(resources)
        }

        let beside = URL(fileURLWithPath: CommandLine.arguments.first ?? ".")
            .deletingLastPathComponent()
            .appendingPathComponent("VoiceConfigs")
        roots.append(beside)

        var out: [String: URL] = [:]
        let fm = FileManager.default

        for root in roots {
            guard let walker = fm.enumerator(at: root,
                                             includingPropertiesForKeys: nil,
                                             options: [.skipsHiddenFiles]) else { continue }
            for case let url as URL in walker {
                let file = url.lastPathComponent
                guard companions.contains(file) else { continue }

                // The voice id is the DIRECTORY the sidecar sits in, not a
                // prefix trimmed off the file name. Both layouts exist upstream
                // (mms-swh/model.onnx.json and mms-swh.model.onnx.json) and
                // guessing from the file name alone silently keys the second one
                // under an empty voice.
                let voice = url.deletingLastPathComponent().lastPathComponent
                guard !voice.isEmpty, voice != root.lastPathComponent else { continue }

                let key = "\(voice)/\(file)"
                if out[key] == nil { out[key] = url }   // first root wins
            }
        }
        return out
    }
}
