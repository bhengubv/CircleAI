/*
 * test_host_tools_ui.c — CircleAI.Hosting.Tools + .GenerativeUI (C11 port).
 *
 * Verifies InMemoryToolCatalog (upsert/get/list/search/by-provider + import),
 * the tool executor seam, JsonRenderParser (strict + lenient), DescribeCatalog,
 * and the recording renderer.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static ca_tool_descriptor_t mk_tool(const char *name, const char *desc, const char *provider,
                                    const char *tag0, const char *tag1) {
    ca_tool_descriptor_t d; memset(&d, 0, sizeof(d));
    d.name = strdup(name);
    d.description = strdup(desc);
    d.provider = strdup(provider);
    d.json_schema = strdup("");
    d.auth_scheme = strdup("none");
    if (tag0) {
        size_t n = tag1 ? 2 : 1;
        d.tags = (char **)calloc(n, sizeof(char *));
        d.tags[0] = strdup(tag0);
        if (tag1) d.tags[1] = strdup(tag1);
        d.tag_count = n;
    }
    return d;
}

static void test_catalog(void) {
    ca_tool_catalog_t *c = ca_tool_catalog_create();
    assert(ca_tool_catalog_count(c) == 0);

    ca_tool_descriptor_t t1 = mk_tool("gmail.send", "Send an email message", "gmail", "communication", "oauth");
    ca_tool_descriptor_t t2 = mk_tool("github.pr", "Open a pull request", "github", "code", NULL);
    ca_tool_descriptor_t t3 = mk_tool("gmail.read", "Read email inbox", "gmail", "communication", NULL);
    assert(ca_tool_catalog_upsert(c, &t1));
    assert(ca_tool_catalog_upsert(c, &t2));
    assert(ca_tool_catalog_upsert(c, &t3));
    assert(ca_tool_catalog_count(c) == 3);

    /* blank name rejected */
    ca_tool_descriptor_t bad = mk_tool("  ", "x", "p", NULL, NULL);
    assert(ca_tool_catalog_upsert(c, &bad) == false);
    ca_tool_descriptor_free(&bad);

    /* get (case-insensitive) */
    ca_tool_descriptor_t got; memset(&got, 0, sizeof(got));
    assert(ca_tool_catalog_get(c, "GMAIL.SEND", &got));
    assert(strcmp(got.provider, "gmail") == 0);
    ca_tool_descriptor_free(&got);

    /* list ordered by name */
    size_t n = 0;
    ca_tool_descriptor_t *list = ca_tool_catalog_list(c, &n);
    assert(n == 3);
    assert(strcmp(list[0].name, "github.pr") == 0); /* 'gi' < 'gm' */
    ca_tool_descriptor_free_array(list, n);

    /* search: "email" hits desc(+2) for gmail.send & gmail.read */
    ca_tool_descriptor_t *res = ca_tool_catalog_search(c, "email", 10, &n);
    assert(n == 2);
    ca_tool_descriptor_free_array(res, n);

    /* search: name substring scores higher */
    res = ca_tool_catalog_search(c, "gmail", 10, &n);
    assert(n == 2);
    /* both are gmail.*; top result should be one of them */
    assert(strstr(res[0].name, "gmail"));
    ca_tool_descriptor_free_array(res, n);

    /* topK limit */
    res = ca_tool_catalog_search(c, "communication", 1, &n);
    assert(n == 1);
    ca_tool_descriptor_free_array(res, n);

    /* blank query -> empty */
    res = ca_tool_catalog_search(c, "   ", 10, &n);
    assert(n == 0 && res == NULL);

    /* by provider */
    res = ca_tool_catalog_list_by_provider(c, "gmail", &n);
    assert(n == 2);
    ca_tool_descriptor_free_array(res, n);

    /* remove */
    assert(ca_tool_catalog_remove(c, "github.pr"));
    assert(ca_tool_catalog_count(c) == 2);
    assert(ca_tool_catalog_remove(c, "nonexistent") == false);

    ca_tool_descriptor_free(&t1); ca_tool_descriptor_free(&t2); ca_tool_descriptor_free(&t3);
    ca_tool_catalog_destroy(c);
    printf("  catalog: ok\n");
}

/* provider seam for import */
static ca_tool_descriptor_t *provider_discover(void *user, size_t *out_count) {
    (void)user;
    ca_tool_descriptor_t *arr = (ca_tool_descriptor_t *)calloc(2, sizeof(ca_tool_descriptor_t));
    arr[0] = mk_tool("mcp.a", "Tool A", "mcp", NULL, NULL);
    arr[1] = mk_tool("mcp.b", "Tool B", "mcp", NULL, NULL);
    *out_count = 2;
    return arr;
}
static bool provider_available(void *user) { (void)user; return true; }

static void test_import_and_executor(void) {
    ca_tool_catalog_t *c = ca_tool_catalog_create();
    ca_tool_provider_t prov = { "mcp", provider_discover, provider_available, NULL };
    int imported = ca_tool_catalog_import_from(c, &prov);
    assert(imported == 2);
    assert(ca_tool_catalog_count(c) == 2);
    ca_tool_catalog_destroy(c);

    /* executor: default null -> error */
    ca_tool_execution_result_t r; memset(&r, 0, sizeof(r));
    ca_tool_descriptor_t t = mk_tool("x", "y", "p", NULL, NULL);
    ca_tool_executor_execute(NULL, &t, "{}", &r);
    assert(r.success == false && r.error);
    ca_tool_execution_result_free(&r);
    ca_tool_descriptor_free(&t);
    printf("  import + executor: ok\n");
}

/* ── generative UI ─────────────────────────────────────────────────────── */

static void test_ui_parse(void) {
    size_t ncat = 0;
    const ca_ui_catalog_entry_t *cat = ca_ui_catalog_default(&ncat);
    assert(ncat == 5);

    /* simple card with children */
    const char *json =
        "{\"kind\":\"card\",\"properties\":{\"title\":\"Hello\"},"
        "\"children\":[{\"kind\":\"textBlock\",\"properties\":{\"text\":\"body\",\"markdown\":false}}]}";
    ca_ui_component_t *root = ca_ui_parse(json, cat, ncat, true);
    assert(root);
    assert(strcmp(root->kind, "card") == 0);
    assert(root->property_count == 1);
    assert(strcmp(root->properties[0].key, "title") == 0);
    assert(root->properties[0].kind == CA_UI_VAL_STRING);
    assert(strcmp(root->properties[0].s, "Hello") == 0);
    assert(root->child_count == 1);
    assert(strcmp(root->children[0].kind, "textBlock") == 0);
    ca_ui_component_free(root);

    /* number + bool property values */
    const char *j2 = "{\"kind\":\"list\",\"properties\":{\"ordered\":true},\"children\":[]}";
    root = ca_ui_parse(j2, cat, ncat, true);
    assert(root && root->property_count == 1);
    assert(root->properties[0].kind == CA_UI_VAL_BOOL && root->properties[0].b == true);
    ca_ui_component_free(root);

    /* strict: unknown kind -> NULL */
    assert(ca_ui_parse("{\"kind\":\"widget\"}", cat, ncat, true) == NULL);
    /* lenient: unknown kind -> textBlock */
    root = ca_ui_parse("{\"kind\":\"widget\"}", cat, ncat, false);
    assert(root && strcmp(root->kind, "textBlock") == 0);
    assert(strstr(root->properties[0].s, "unknown kind"));
    ca_ui_component_free(root);

    /* strict: undeclared property -> NULL */
    assert(ca_ui_parse("{\"kind\":\"button\",\"properties\":{\"bogus\":\"x\"}}", cat, ncat, true) == NULL);

    /* strict: children on non-container -> NULL */
    assert(ca_ui_parse("{\"kind\":\"button\",\"properties\":{\"label\":\"a\",\"action\":\"b\"},\"children\":[{\"kind\":\"textBlock\"}]}", cat, ncat, true) == NULL);

    /* missing kind -> NULL */
    assert(ca_ui_parse("{\"properties\":{}}", cat, ncat, true) == NULL);

    printf("  ui parse: ok\n");
}

static void test_ui_prompt_and_renderer(void) {
    size_t ncat = 0;
    const ca_ui_catalog_entry_t *cat = ca_ui_catalog_default(&ncat);
    char *prompt = ca_ui_describe_catalog_for_prompt(cat, ncat);
    assert(strstr(prompt, "Allowed kinds:"));
    assert(strstr(prompt, "card"));
    assert(strstr(prompt, "children: array of components"));
    free(prompt);

    ca_recording_ui_renderer_t *r = ca_recording_ui_renderer_create();
    ca_ui_renderer_t rv = ca_recording_ui_renderer_as_renderer(r);
    assert(ca_recording_ui_renderer_count(r) == 0);
    ca_ui_component_t *root = ca_ui_parse("{\"kind\":\"textBlock\",\"properties\":{\"text\":\"hi\"}}", cat, ncat, true);
    rv.render(rv.user, root);
    assert(ca_recording_ui_renderer_count(r) == 1);
    assert(strcmp(ca_recording_ui_renderer_last_kind(r), "textBlock") == 0);
    ca_ui_component_free(root);
    ca_recording_ui_renderer_destroy(r);
    printf("  ui prompt + renderer: ok\n");
}

int main(void) {
    test_catalog();
    test_import_and_executor();
    test_ui_parse();
    test_ui_prompt_and_renderer();
    printf("test_host_tools_ui: all assertions passed\n");
    return 0;
}
