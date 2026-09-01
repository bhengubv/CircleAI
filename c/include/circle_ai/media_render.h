#ifndef CIRCLE_AI_MEDIA_RENDER_H
#define CIRCLE_AI_MEDIA_RENDER_H

/*
 * media_render.h - CircleAI.Media.Rendering (C11).
 *
 * Programmatic - NOT generative - media. A spec describes a canvas: a
 * background, a stack of the person's OWN photos, text overlays, and a
 * timeline. It is pure data with no rendering dependency, so a host can build,
 * serialise or template it freely.
 *
 * THE HONEST SPLIT. The renderer, the APNG encoder, the PNG/BMP decoder and
 * every Null default are here in pure C. The genuinely device-specific pieces -
 * a real H.264 muxer and an HTML rasteriser - are seams a host fills, and the
 * Null video encoder is the honest marker for that: it advertises "video/mp4"
 * and emits zero bytes, because a real MP4 encoder is not feasible in portable
 * C on a low-end phone.
 *
 * COORDINATES ARE NORMALISED. A layout written in pixels is a layout that only
 * works at one size, and the same spec has to render a 1080x1920 story and a
 * 540x960 preview.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── geometry and colour ──────────────────────────────────────────────────── */

typedef struct {
    int width;
    int height;
} ca_render_size_t;

extern const ca_render_size_t CA_RENDER_SIZE_SQUARE_1080;
extern const ca_render_size_t CA_RENDER_SIZE_PORTRAIT_1080X1920;
extern const ca_render_size_t CA_RENDER_SIZE_LANDSCAPE_1920X1080;
extern const ca_render_size_t CA_RENDER_SIZE_PREVIEW_540X960;

/* 0..1 of the canvas, so one spec renders at any size. */
typedef struct {
    double x, y, w, h;
} ca_norm_rect_t;

ca_norm_rect_t ca_norm_rect_full(void);

typedef struct {
    double x, y;
} ca_norm_vec_t;

typedef struct {
    uint8_t r, g, b, a;
} ca_rgba32_t;

ca_rgba32_t ca_rgba32(uint8_t r, uint8_t g, uint8_t b, uint8_t a);
ca_rgba32_t ca_rgba32_rgb(uint8_t r, uint8_t g, uint8_t b);

/* #RGB, #RRGGBB or #RRGGBBAA, with or without the hash.
 *
 * The three-digit form DUPLICATES each nibble, so #f00 is ff0000 and not
 * f00000 — halving it instead makes every short colour darker than written. */
bool ca_rgba32_parse(const char *hex, ca_rgba32_t *out_colour);

extern const ca_rgba32_t CA_RGBA32_TRANSPARENT;
extern const ca_rgba32_t CA_RGBA32_BLACK;
extern const ca_rgba32_t CA_RGBA32_WHITE;

/* ── the raster ───────────────────────────────────────────────────────────── */

/* RGBA8888, row-major, no padding. */
typedef struct {
    int width;
    int height;
    uint8_t *pixels;      /* width * height * 4 */
} ca_pixel_buffer_t;

ca_pixel_buffer_t *ca_pixel_buffer_new(int width, int height);
void ca_pixel_buffer_free(ca_pixel_buffer_t *buffer);

/* NULL when out of range, rather than reading past the end: a compositor that
 * walks one pixel too far is a bug worth surviving. */
bool ca_pixel_buffer_get(const ca_pixel_buffer_t *buffer, int x, int y,
                         ca_rgba32_t *out_colour);

typedef struct ca_raster_canvas ca_raster_canvas_t;

ca_raster_canvas_t *ca_raster_canvas_new(int width, int height);
void ca_raster_canvas_free(ca_raster_canvas_t *canvas);

void ca_raster_canvas_fill(ca_raster_canvas_t *canvas, ca_rgba32_t colour);

/* Source-over alpha compositing. Straight alpha, not premultiplied, because
 * that is what the decoders above produce and converting twice loses a bit of
 * every semi-transparent edge. */
void ca_raster_canvas_blend(ca_raster_canvas_t *canvas, int x, int y,
                            ca_rgba32_t colour);

void ca_raster_canvas_draw(ca_raster_canvas_t *canvas,
                           const ca_pixel_buffer_t *source,
                           ca_norm_rect_t rect, int fit, double opacity);

/* Borrowed; freed with the canvas. */
const ca_pixel_buffer_t *ca_raster_canvas_buffer(const ca_raster_canvas_t *canvas);

/* ── the font ─────────────────────────────────────────────────────────────── */

/*
 * A 5x7 pixel font with no external file.
 *
 * Lower case FOLDS to upper: the glyph table has one case, so a mixed-case
 * caption still renders rather than losing half its letters. Rich typography
 * and emoji are the HTML seam's job, not this one's.
 */
typedef struct ca_bitmap_font ca_bitmap_font_t;

const ca_bitmap_font_t *ca_bitmap_font_default(void);

int ca_bitmap_font_glyph_width(const ca_bitmap_font_t *font);
int ca_bitmap_font_glyph_height(const ca_bitmap_font_t *font);

/* Measures without drawing, so a caller can centre before it commits. */
int ca_bitmap_font_measure(const ca_bitmap_font_t *font, const char *text, int scale);

void ca_bitmap_font_draw(const ca_bitmap_font_t *font, ca_raster_canvas_t *canvas,
                         const char *text, int x, int y, int scale, ca_rgba32_t colour);

/* ── the spec ─────────────────────────────────────────────────────────────── */

typedef enum { CA_CONTENT_FIT_FILL = 0, CA_CONTENT_FIT_CONTAIN, CA_CONTENT_FIT_COVER } ca_content_fit_t;
typedef enum { CA_TEXT_ALIGN_LEFT = 0, CA_TEXT_ALIGN_CENTER, CA_TEXT_ALIGN_RIGHT } ca_text_align_t;
typedef enum { CA_EASING_LINEAR = 0, CA_EASING_IN, CA_EASING_OUT, CA_EASING_IN_OUT } ca_easing_kind_t;

/* Fractions of the WHOLE CLIP, so a spec does not care how many frames it ends
 * up being rendered at. */
typedef struct {
    double start_fraction, end_fraction;
    double from_opacity, to_opacity;
    double from_scale, to_scale;
    ca_norm_vec_t from_translate, to_translate;
    ca_easing_kind_t easing;
} ca_motion_t;

ca_motion_t ca_motion_none(void);
ca_motion_t ca_motion_fade_in(void);
ca_motion_t ca_motion_fade_out(void);
ca_motion_t ca_motion_ken_burns(void);

/* Where a layer's pixels come from. A tagged union rather than two types: the
 * two are told apart at exactly one place, the decode. */
typedef enum { CA_IMAGE_SOURCE_RAW = 0, CA_IMAGE_SOURCE_ENCODED } ca_image_source_kind_t;

typedef struct {
    ca_image_source_kind_t kind;
    uint8_t *data;       /* raw RGBA, or encoded bytes */
    size_t data_len;
    int width, height;   /* raw only */
    char *mime_hint;     /* encoded only */
} ca_image_source_t;

void ca_image_source_free(ca_image_source_t *source);

ca_image_source_t *ca_image_source_raw(const uint8_t *rgba, size_t len, int w, int h);
ca_image_source_t *ca_image_source_encoded(const uint8_t *bytes, size_t len,
                                           const char *mime_hint);

typedef struct {
    ca_image_source_t *source;
    ca_norm_rect_t rect;
    ca_content_fit_t fit;
    double opacity;
    ca_motion_t motion;
    bool has_motion;
    int z_order;
    char *id;
} ca_image_layer_t;

void ca_image_layer_free(ca_image_layer_t *layer);

typedef struct {
    char *text;
    ca_norm_rect_t rect;
    /* Of the canvas HEIGHT, so type scales with the frame rather than being
     * fixed in points that mean nothing at two different sizes. */
    double font_height_fraction;
    ca_rgba32_t colour;
    ca_text_align_t align;
    ca_rgba32_t box_colour;
    double letter_spacing_fraction;
    double line_spacing_fraction;
    ca_motion_t motion;
    bool has_motion;
    int z_order;
    char *id;
} ca_text_overlay_t;

void ca_text_overlay_free(ca_text_overlay_t *overlay);

typedef struct {
    char *html;
    char **token_keys;
    char **token_values;
    size_t token_count;
} ca_html_template_source_t;

void ca_html_template_source_free(ca_html_template_source_t *source);

typedef struct {
    ca_render_size_t size;
    ca_rgba32_t background;
    ca_image_layer_t *images;
    size_t image_count;
    ca_text_overlay_t *texts;
    size_t text_count;
    /* Zero or less means a still. */
    double duration_seconds;
    int frame_rate;
    ca_html_template_source_t *html;
} ca_media_spec_t;

ca_media_spec_t *ca_media_spec_new(ca_render_size_t size, ca_rgba32_t background);
void ca_media_spec_free(ca_media_spec_t *spec);

bool ca_media_spec_is_still(const ca_media_spec_t *spec);

/* At least ONE frame, always. A 0.01 s clip is still a frame, not nothing. */
size_t ca_media_spec_frame_count(const ca_media_spec_t *spec);

/* ── codecs ───────────────────────────────────────────────────────────────── */

/* PNG. Encoding writes colour type 6 (RGBA); decoding handles 8-bit
 * non-interlaced colour types 0, 2, 4 and 6. */
uint8_t *ca_image_encode_png(const ca_pixel_buffer_t *image, size_t *out_len);
ca_pixel_buffer_t *ca_image_decode_png(const uint8_t *bytes, size_t len);

/* BMP. Every row is padded to a four-byte boundary; forgetting that shears the
 * image progressively — right at the top-left and wrong by the bottom-right. */
uint8_t *ca_image_encode_bmp(const ca_pixel_buffer_t *image, size_t *out_len);
ca_pixel_buffer_t *ca_image_decode_bmp(const uint8_t *bytes, size_t len);

/* The decoder seam. JPEG is NAMED rather than lumped in with "unrecognised": a
 * JPEG is a picture, and the caller needs to know it must wire a platform
 * decoder, not that the file is broken. */
typedef enum {
    CA_IMAGE_DECODE_OK = 0,
    CA_IMAGE_DECODE_UNRECOGNISED,
    CA_IMAGE_DECODE_JPEG_NEEDS_PLATFORM_DECODER,
    CA_IMAGE_DECODE_UNSUPPORTED
} ca_image_decode_status_t;

typedef struct ca_image_decoder {
    void *state;
    const char *(*backend_id)(void *state);
    /* NULL on failure; `out_status` says why. Returning NULL rather than
     * throwing means an undecodable LAYER is skipped and the rest of the
     * composition still renders. */
    ca_pixel_buffer_t *(*decode)(void *state, const uint8_t *bytes, size_t len,
                                 const char *mime_hint,
                                 ca_image_decode_status_t *out_status);
    void (*free_fn)(void *state);
} ca_image_decoder_t;

void ca_image_decoder_free(ca_image_decoder_t *decoder);

/* PNG and BMP, in pure C. Everything else is somebody else's decoder. */
ca_image_decoder_t *ca_managed_image_decoder_new(void);

/* ── clips ────────────────────────────────────────────────────────────────── */

typedef struct {
    ca_render_size_t size;
    int frame_rate;
    int frame_count;
    /* 0 means loop forever, which is what a bed under a caption wants. */
    int loop_count;
} ca_clip_encode_options_t;

typedef struct {
    uint8_t *bytes;
    size_t byte_count;
    char *mime_type;
    int frame_count;
    ca_render_size_t size;
    int frame_rate;
    char *backend_id;
} ca_encoded_clip_t;

void ca_encoded_clip_free(ca_encoded_clip_t *clip);

typedef struct ca_video_encoder {
    void *state;
    const char *(*backend_id)(void *state);
    const char *(*output_mime_type)(void *state);
    ca_encoded_clip_t *(*encode)(void *state, const ca_pixel_buffer_t *const *frames,
                                 size_t frame_count,
                                 const ca_clip_encode_options_t *options);
    void (*free_fn)(void *state);
} ca_video_encoder_t;

void ca_video_encoder_free(ca_video_encoder_t *encoder);

/* Animated PNG: a real clip this module can actually produce. */
ca_video_encoder_t *ca_animated_png_encoder_new(void);

/*
 * The HONEST GAP MARKER for true video: advertises "video/mp4" and emits zero
 * bytes. A real H.264 clip needs a genuine encoder, which is not feasible in
 * portable C on a low-end phone — the on-device, de-Googled path is AOSP
 * MediaCodec or FFmpeg wired in from the host. Frames are deliberately NOT
 * consumed, and the INTENDED length is reported from the options so a caller
 * can still see what it asked for.
 */
ca_video_encoder_t *ca_null_video_encoder_new(void);

/* ── the HTML seam ────────────────────────────────────────────────────────── */

typedef struct ca_html_frame_provider {
    void *state;
    const char *(*backend_id)(void *state);
    ca_pixel_buffer_t **(*render)(void *state, const ca_html_template_source_t *html,
                                  ca_render_size_t size, int frame_count, int frame_rate,
                                  size_t *out_count);
    void (*free_fn)(void *state);
} ca_html_frame_provider_t;

void ca_html_frame_provider_free(ca_html_frame_provider_t *provider);

ca_html_frame_provider_t *ca_null_html_frame_provider_new(void);

/* ── the renderer ─────────────────────────────────────────────────────────── */

typedef struct ca_media_renderer {
    void *state;
    const char *(*backend_id)(void *state);
    ca_pixel_buffer_t *(*render_still)(void *state, const ca_media_spec_t *spec,
                                       double poster_fraction);
    ca_pixel_buffer_t **(*frames)(void *state, const ca_media_spec_t *spec,
                                  size_t *out_count);
    ca_encoded_clip_t *(*render_clip)(void *state, const ca_media_spec_t *spec,
                                      ca_video_encoder_t *encoder);
    void (*free_fn)(void *state);
} ca_media_renderer_t;

void ca_media_renderer_free(ca_media_renderer_t *renderer);

/* Composes a spec onto a raster canvas, frame by frame. The first frame is at
 * progress 0 and the last at 1, so a fade-in starts fully transparent and a
 * fade-out ends fully gone. */
ca_media_renderer_t *ca_managed_media_renderer_new(ca_image_decoder_t *decoder,
                                                   const ca_bitmap_font_t *font);

/* A 1x1 still and no frames. Not NULL: a caller compositing a poster wants
 * something with a size it can reason about. */
ca_media_renderer_t *ca_null_media_renderer_new(void);

/* ── templates ────────────────────────────────────────────────────────────── */

/* A 1x1 solid colour, scaled to whatever rectangle it lands in — so a
 * full-screen scrim costs four bytes. */
ca_image_source_t *ca_media_template_solid_colour(ca_rgba32_t colour);

/*
 * A short social ad.
 *
 * THE SCRIM IS THE POINT. White text over an arbitrary photo is legible or not
 * depending on the photo, and the one thing nobody checks before posting is
 * every frame. A half-height dark band under the text makes it legible over
 * anything.
 */
ca_media_spec_t *ca_media_template_social_ad(ca_render_size_t size,
                                             ca_image_source_t *background,
                                             const char *headline,
                                             const char *subline);

ca_media_spec_t *ca_media_template_video_cv_card(ca_render_size_t size,
                                                 ca_image_source_t *portrait,
                                                 const char *name,
                                                 const char *title,
                                                 const char *contact);

/* White background, not the house navy: an HTML scene brings its own styling
 * and a dark canvas shows through every unstyled margin. */
ca_media_spec_t *ca_media_template_from_html(ca_render_size_t size, const char *html);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MEDIA_RENDER_H */
