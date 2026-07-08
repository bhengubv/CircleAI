/*
 * test_capability_registry.c — ExternalCapabilityRegistry (C11).
 *
 * Asserts the ported registry matches CapabilityRegistry.cs: 30 entries, exact
 * fields for spot-checked slugs, case-insensitive Find, and ByPackage grouping.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

int main(void) {
    size_t n = 0;
    const ca_capability_entry_t *all = ca_capability_registry_all(&n);
    assert(all != NULL);
    assert(n == 30);                                    /* All.Count */
    assert(ca_capability_registry_count() == 30);

    /* first entry: claude-mem */
    assert(strcmp(all[0].id, "claude-mem") == 0);
    assert(strcmp(all[0].repo, "thedotmack/claude-mem") == 0);
    assert(strcmp(all[0].license, "MIT") == 0);
    assert(strcmp(all[0].strategy, "pattern-port") == 0);
    assert(strcmp(all[0].target_package, "CircleAI.Memory") == 0);
    assert(all[0].value_count == 10);
    assert(strcmp(all[0].value_bullets[0], "Multi-platform memory adapter") == 0);
    assert(strcmp(all[0].value_bullets[9], "Token economy tracking") == 0);

    /* last entry: awesome-design-md */
    assert(strcmp(all[29].id, "awesome-design-md") == 0);
    assert(strcmp(all[29].license, "CC-BY-4.0") == 0);
    assert(strcmp(all[29].target_package, "CircleAI.Skills.PackSources") == 0);
    assert(all[29].value_count == 1);

    /* Find is case-insensitive (OrdinalIgnoreCase) */
    const ca_capability_entry_t *e = ca_capability_registry_find("HippoRAG");
    assert(e && strcmp(e->repo, "OSU-NLP-Group/HippoRAG") == 0);
    assert(e->value_count == 2);
    e = ca_capability_registry_find("hipporag");
    assert(e && strcmp(e->id, "HippoRAG") == 0);
    e = ca_capability_registry_find("AMPHION");
    assert(e && strcmp(e->target_package, "CircleAI.Speech") == 0);
    assert(ca_capability_registry_find("does-not-exist") == NULL);
    assert(ca_capability_registry_find(NULL) == NULL);

    /* Amphion's 10 bullets verbatim spot-check */
    e = ca_capability_registry_find("Amphion");
    assert(e->value_count == 10);
    assert(strcmp(e->value_bullets[0], "FastSpeech2/VITS/VALLE/NaturalSpeech2") == 0);

    /* the UTF-8 arrow bullet is byte-preserved (U+2192 = \xe2\x86\x92) */
    e = ca_capability_registry_find("show-me-the-money");
    assert(strstr(e->value_bullets[0], "\xe2\x86\x92 revenue") != NULL);
    e = ca_capability_registry_find("Observer AI");
    assert(strstr(e->value_bullets[0], "sensors\xe2\x86\x92models\xe2\x86\x92tools") != NULL);

    /* ByPackage: CircleAI.Speech has Amphion + yapsnap (2) */
    size_t m = 0;
    const ca_capability_entry_t **speech = ca_capability_registry_by_package("CircleAI.Speech", &m);
    assert(m == 2);
    assert(speech != NULL);
    bool saw_amphion = false, saw_yapsnap = false;
    for (size_t i = 0; i < m; ++i) {
        if (strcmp(speech[i]->id, "Amphion") == 0) saw_amphion = true;
        if (strcmp(speech[i]->id, "yapsnap") == 0) saw_yapsnap = true;
    }
    assert(saw_amphion && saw_yapsnap);
    free((void *)speech);

    /* CircleAI.Games: aimangastudio + flame (2) */
    const ca_capability_entry_t **games = ca_capability_registry_by_package("circleai.games", &m);
    assert(m == 2);   /* case-insensitive package match */
    free((void *)games);

    /* CircleAI.Inference: airllm + shard (2) */
    const ca_capability_entry_t **inf = ca_capability_registry_by_package("CircleAI.Inference", &m);
    assert(m == 2);
    free((void *)inf);

    /* CircleAI.Skills: superpowers + Anthropic-Cybersecurity-Skills (2) */
    const ca_capability_entry_t **skills = ca_capability_registry_by_package("CircleAI.Skills", &m);
    assert(m == 2);
    free((void *)skills);

    /* no matches → NULL + 0 */
    const ca_capability_entry_t **none = ca_capability_registry_by_package("CircleAI.Nonexistent", &m);
    assert(none == NULL && m == 0);

    /* NULL arg → NULL + SIZE_MAX */
    const ca_capability_entry_t **bad = ca_capability_registry_by_package(NULL, &m);
    assert(bad == NULL && m == (size_t)-1);

    /* mythology entries would have NULL repo — none in this set, so all repos non-NULL */
    for (size_t i = 0; i < n; ++i) assert(all[i].repo != NULL);

    printf("test_capability_registry: all assertions passed\n");
    return 0;
}
