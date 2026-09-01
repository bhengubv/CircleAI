// CoreModelCatalog.swift
//
// What a model IS, where it comes from, where it lives, and what a download is
// doing right now.
//
// Ported from src/CircleAI.Core/ModelModality.cs, ModelSource.cs, ModelPaths.cs
// and IModelSource.cs.

import Foundation

/// What a model DOES. Kept separate from its size or its backend, because those
/// change with the build and this does not.
public enum ModelModality: Int, Sendable, Equatable, Codable, CaseIterable {
    case chat = 0
    case asr
    case tts
    case vad
    case wakeWord
    case vision
    case music
    case video
    case coding
    case phonemizer
}

/// Where a model's bytes come from.
///
/// `huggingFaceBucket` is a bucket we do not hold the token for, which is why it
/// is a separate case from `huggingFace` rather than a URL detail: a 401 from a
/// bucket is not the same problem as a 404 from a repo, and treating them alike
/// sends somebody looking for a file that is there.
public enum ModelSource: Int, Sendable, Equatable, Codable, CaseIterable {
    case modelScope = 0
    case huggingFace = 1
    case huggingFaceBucket = 2
    case gitHubRelease = 3
}

/// What a download is doing right now — not all of it is transfer.
///
/// A 433 MB bundle spends real time hashing and, on a bad link, retrying.
/// Without a phase those look identical to a stalled download, and the person
/// watching concludes the app has hung.
public enum DownloadPhase: Int, Sendable, Equatable, Codable, CaseIterable {
    /// Bytes are moving.
    case downloading = 0
    /// Continuing a partial file rather than starting over.
    case resuming
    /// Waiting out a backoff before another attempt.
    case retrying
    /// Checking SHA-256. No bytes move; can take seconds on a phone.
    case verifying
    /// Already on disk and valid — skipped.
    case cached
    /// Every file present and verified.
    case complete
}

/// The ONE place the model directory is decided.
///
/// IT WAS DECIDED IN FOUR PLACES AND THEY DISAGREED ON A PHONE. Three loaders
/// defaulted to the application-data folder and the mobile head used the app's
/// own data directory. On a desktop those are the same folder and nothing was
/// ever wrong. On Android they are not — the first is a SUBDIRECTORY of the
/// second, which is why nothing failed and nothing was noticed. Both paths
/// existed, both were writable, both looked right in a log. What happened
/// instead is that a 523 MB chat model was downloaded twice onto a phone with
/// 890 MB of app data: one copy where the app looks for it, one copy where a
/// caller that forgot to pass a path put it.
///
/// FOUND BY LOOKING AT THE DISK, not by anything failing. That is the shape of
/// this bug — two owners of one fact, agreeing everywhere it is cheap to check
/// and disagreeing on the device the product is for.
public enum ModelPaths {

    /// The platform's own per-user data directory.
    ///
    /// On iOS and Android this is the app's sandboxed documents directory; on a
    /// desktop it is application support. Deliberately NOT a cache directory:
    /// the system is free to evict a cache under pressure, and a half-evicted
    /// 400 MB bundle fails its hash on next launch with no explanation.
    public static var root: String {
        #if os(iOS) || os(tvOS) || os(watchOS) || os(Android)
        let search = FileManager.SearchPathDirectory.documentDirectory
        #else
        let search = FileManager.SearchPathDirectory.applicationSupportDirectory
        #endif

        if let url = FileManager.default.urls(for: search, in: .userDomainMask).first {
            return url.path
        }
        return NSHomeDirectory()
    }

    public static var `default`: String {
        (root as NSString).appendingPathComponent("CircleAI/Models")
    }

    /// The directory to use, created if it is not there.
    ///
    /// Blank means "wherever the default is" rather than the current working
    /// directory: a relative path here would put a 400 MB download wherever the
    /// process happened to be started from.
    @discardableResult
    public static func resolve(_ requested: String?) -> String {
        let dir: String
        if let requested, !requested.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            dir = requested
        } else {
            dir = `default`
        }
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        return dir
    }
}
