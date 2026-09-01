/**
 * workflows/paca_docs
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_docs.ts
//
// (3.3.0) Project-level living documents with folders, version snapshots,
// activity feed, task/epic linkage, and @mentions of humans + agents (paca
// port, PacaDocs.cs).

import { randomUUID } from "../platform";

/** (3.3.0) A doc node (folder OR document). Mirrors C# `DocNode`. */
export interface DocNode {
  readonly id: string;
  readonly projectId: string;
  readonly parentId: string | null;
  readonly isFolder: boolean;
  readonly title: string;
  readonly contentJson: string;
  readonly createdAtUtc: Date;
  readonly deletedAtUtc: Date | null;
}

/** (3.3.0) One immutable snapshot of a doc. Mirrors C# `DocVersion`. */
export interface DocVersion {
  readonly versionId: string;
  readonly docId: string;
  readonly contentJson: string;
  readonly savedAtUtc: Date;
  readonly authorMemberId: string;
}

/** (3.3.0) One document-activity event. Mirrors C# `DocActivity`. */
export interface DocActivity {
  readonly activityId: string;
  readonly docId: string;
  readonly authorMemberId: string;
  /** "created" / "edited" / "ai-edited" / "linked" / "commented". */
  readonly action: string;
  readonly detail: string | null;
  readonly at: Date;
}

/** (3.3.0) Link between a doc section and a task / epic. Mirrors C# `DocLink`. */
export interface DocLink {
  readonly linkId: string;
  readonly docId: string;
  readonly sectionAnchor: string;
  readonly projectId: string;
  readonly taskNumber: number;
}

/** Return type of {@link PacaDocService.diffLines}. */
export interface DocDiff {
  readonly added: readonly string[];
  readonly removed: readonly string[];
}

// @([a-zA-Z0-9_\-]+) — mirrors the C# compiled MentionPattern.
const MENTION_PATTERN = /@([a-zA-Z0-9_-]+)/g;

/** (3.3.0) In-memory doc service. Mirrors C# `PacaDocService`. */
export class PacaDocService {
  private readonly nodes = new Map<string, DocNode>();
  private readonly versions = new Map<string, DocVersion[]>();
  private readonly activity = new Map<string, DocActivity[]>();
  private readonly links = new Map<string, DocLink[]>();
  private readonly clock: () => Date;

  constructor(clock?: (() => Date) | null) {
    this.clock = clock ?? ((): Date => new Date());
  }

  createFolder(id: string, projectId: string, parentId: string | null, title: string): DocNode {
    return this.create(id, projectId, parentId, true, title, "{}", "system");
  }

  createDocument(
    id: string,
    projectId: string,
    parentId: string | null,
    title: string,
    contentJson: string,
    authorMemberId: string,
  ): DocNode {
    return this.create(id, projectId, parentId, false, title, contentJson, authorMemberId);
  }

  private create(
    id: string,
    projectId: string,
    parentId: string | null,
    isFolder: boolean,
    title: string,
    contentJson: string,
    authorMemberId: string,
  ): DocNode {
    if (isBlank(id)) throw new Error("id required");
    if (isBlank(projectId)) throw new Error("projectId required");
    const node: DocNode = {
      id,
      projectId,
      parentId,
      isFolder,
      title: title ?? "",
      contentJson: contentJson ?? "{}",
      createdAtUtc: this.clock(),
      deletedAtUtc: null,
    };
    if (this.nodes.has(id)) throw new Error(`Doc '${id}' already exists.`);
    this.nodes.set(id, node);

    if (!isFolder) {
      this.versions.set(id, []);
      this.activity.set(id, [
        { activityId: newId(), docId: id, authorMemberId, action: "created", detail: null, at: this.clock() },
      ]);
    }
    return node;
  }

  get(id: string): DocNode | null {
    const n = this.nodes.get(id);
    return n !== undefined && n.deletedAtUtc === null ? n : null;
  }

  listChildren(projectId: string, parentId: string | null): readonly DocNode[] {
    return [...this.nodes.values()]
      .filter((n) => n.projectId === projectId && n.parentId === parentId && n.deletedAtUtc === null)
      .sort((a, b) => a.title.localeCompare(b.title));
  }

  /** (3.3.0) Edit a document: writes a new version + activity entry, returns mentioned handles. */
  edit(id: string, newContentJson: string, authorMemberId: string, isAiEdit = false): readonly string[] {
    const node = this.nodes.get(id);
    if (node === undefined || node.isFolder || node.deletedAtUtc !== null) {
      throw new Error(`Doc '${id}' is not editable.`);
    }

    const updated: DocNode = { ...node, contentJson: newContentJson ?? "{}" };
    this.nodes.set(id, updated);

    const version: DocVersion = {
      versionId: newId(),
      docId: id,
      contentJson: node.contentJson,
      savedAtUtc: this.clock(),
      authorMemberId,
    };
    this.versionBucket(id).push(version);

    this.activityBucket(id).push({
      activityId: newId(),
      docId: id,
      authorMemberId,
      action: isAiEdit ? "ai-edited" : "edited",
      detail: null,
      at: this.clock(),
    });

    return extractMentions(newContentJson ?? "");
  }

  versionsOf(docId: string): readonly DocVersion[] {
    const list = this.versions.get(docId);
    return list === undefined ? [] : [...list];
  }

  /** (3.3.0) Cheap diff between two versions — returns added + removed text lines. */
  diffLines(before: string, after: string): DocDiff {
    const b = new Set((before ?? "").split("\n"));
    const a = new Set((after ?? "").split("\n"));
    const added = [...a].filter((x) => !b.has(x));
    const removed = [...b].filter((x) => !a.has(x));
    return { added, removed };
  }

  activityOf(docId: string): readonly DocActivity[] {
    const list = this.activity.get(docId);
    return list === undefined ? [] : [...list];
  }

  link(docId: string, sectionAnchor: string, projectId: string, taskNumber: number): DocLink {
    const link: DocLink = { linkId: newId(), docId, sectionAnchor, projectId, taskNumber };
    let bucket = this.links.get(docId);
    if (bucket === undefined) {
      bucket = [];
      this.links.set(docId, bucket);
    }
    bucket.push(link);
    this.activityBucket(docId).push({
      activityId: newId(),
      docId,
      authorMemberId: "system",
      action: "linked",
      detail: `${projectId}-${taskNumber}@${sectionAnchor}`,
      at: this.clock(),
    });
    return link;
  }

  linksOf(docId: string): readonly DocLink[] {
    const list = this.links.get(docId);
    return list === undefined ? [] : [...list];
  }

  private versionBucket(id: string): DocVersion[] {
    let list = this.versions.get(id);
    if (list === undefined) {
      list = [];
      this.versions.set(id, list);
    }
    return list;
  }

  private activityBucket(id: string): DocActivity[] {
    let list = this.activity.get(id);
    if (list === undefined) {
      list = [];
      this.activity.set(id, list);
    }
    return list;
  }
}

function extractMentions(content: string): readonly string[] {
  const set = new Set<string>();
  // Match case-insensitively but preserve first-seen casing, deduping case-insensitively.
  const seen = new Set<string>();
  for (const m of content.matchAll(MENTION_PATTERN)) {
    const handle = m[1];
    const lower = handle.toLowerCase();
    if (!seen.has(lower)) {
      seen.add(lower);
      set.add(handle);
    }
  }
  return [...set];
}

function newId(): string {
  return randomUUID().replace(/-/g, "");
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
