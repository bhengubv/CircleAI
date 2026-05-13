#ifndef CIRCLE_AI_INFERENCE_H
#define CIRCLE_AI_INFERENCE_H

typedef struct {
    const char* model;         /* NULL = default */
    int         max_tokens;    /* 0 = default */
    float       temperature;   /* -1 = default */
    int         stream;        /* 0 = false */
} ca_generation_options_t;

#endif /* CIRCLE_AI_INFERENCE_H */
