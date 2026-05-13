#ifndef CIRCLE_AI_TOOLS_H
#define CIRCLE_AI_TOOLS_H

typedef enum {
    CA_PARAM_STRING = 0, CA_PARAM_NUMBER, CA_PARAM_BOOLEAN, CA_PARAM_OBJECT, CA_PARAM_ARRAY
} ca_param_type_t;

typedef struct {
    const char*    name;
    const char*    description;
    ca_param_type_t type;
    int            required; /* 0 = false */
} ca_tool_parameter_t;

#define CA_MAX_PARAMS 16

typedef struct {
    const char*        name;
    const char*        description;
    ca_tool_parameter_t params[CA_MAX_PARAMS];
    int                param_count;
} ca_tool_definition_t;

typedef struct {
    char        invocation_id[37];
    const char* tool_name;
    const char* arguments_json;
} ca_tool_invocation_t;

typedef struct {
    char        invocation_id[37];
    int         success;          /* 0 = false */
    const char* result_json;      /* NULL on error */
    const char* error_message;    /* NULL on success */
} ca_tool_result_t;

#endif /* CIRCLE_AI_TOOLS_H */
