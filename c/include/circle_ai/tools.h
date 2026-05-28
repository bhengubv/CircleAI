#ifndef CIRCLE_AI_TOOLS_H
#define CIRCLE_AI_TOOLS_H

/*
 * tools.h — ToolDefinition, ToolInvocation, and ToolResult types.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>

/* ---------------------------------------------------------------------------
 * ToolParameter
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_PARAM_STRING  = 0,
    CA_PARAM_NUMBER  = 1,
    CA_PARAM_BOOLEAN = 2,
    CA_PARAM_OBJECT  = 3,
    CA_PARAM_ARRAY   = 4
} ca_param_type_t;

typedef struct {
    const char     *name;
    const char     *description;
    ca_param_type_t type;
    int             required; /* non-zero = required */
} ca_tool_parameter_t;

/* ---------------------------------------------------------------------------
 * ToolDefinition
 * --------------------------------------------------------------------------- */

#define CA_MAX_PARAMS 16

typedef struct {
    const char         *name;
    const char         *description;
    ca_tool_parameter_t params[CA_MAX_PARAMS];
    int                 param_count;
} ca_tool_definition_t;

/* ---------------------------------------------------------------------------
 * ToolInvocation — a request to call a tool
 * --------------------------------------------------------------------------- */

typedef struct {
    char        invocation_id[37]; /* UUID string */
    const char *tool_name;         /* caller owns */
    const char *arguments_json;    /* JSON object string; caller owns */
    int64_t     requested_at_ms;   /* Unix ms UTC */
} ca_tool_invocation_t;

/* ---------------------------------------------------------------------------
 * ToolResult — response from a tool call
 * --------------------------------------------------------------------------- */

typedef struct {
    char        invocation_id[37]; /* matches the originating invocation */
    int         success;           /* non-zero = success */
    const char *result_json;       /* JSON; NULL on error; caller owns   */
    const char *error_message;     /* NULL on success; caller owns       */
    int64_t     completed_at_ms;   /* Unix ms UTC */
} ca_tool_result_t;

#endif /* CIRCLE_AI_TOOLS_H */
