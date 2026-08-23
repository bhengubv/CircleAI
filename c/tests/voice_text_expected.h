/* voice_text_expected.h — GENERATED from the fixtures; see test_voice_text.c. */

#define SPLITTER_MAX_CHARS 220

static const seg_expect SPLIT_2[] = {
    { "Sawubona.", 280 },
    { "Unjani?", 0 },
};
static const seg_expect SPLIT_3[] = {
    { "Listen:", 200 },
    { "this matters;", 200 },
    { "then go.", 280 },
    { "Done!", 0 },
};
static const seg_expect SPLIT_4[] = {
    { "It costs 3.5 rand.", 280 },
    { "Really.", 0 },
};
static const seg_expect SPLIT_5[] = {
    { "Visit thegeek.co.za for more.", 280 },
    { "Thanks.", 0 },
};
static const seg_expect SPLIT_6[] = {
    { "\xe0" "\xa4" "\xa8" "\xe0" "\xa4" "\xae" "\xe0" "\xa4" "\xb8" "\xe0" "\xa5" "\x8d" "\xe0" "\xa4" "\xa4" "\xe0" "\xa5" "\x87" "\xe0" "\xa5" "\xa4" "", 280 },
    { "\xe0" "\xa4" "\x86" "\xe0" "\xa4" "\xaa" " \xe0" "\xa4" "\x95" "\xe0" "\xa5" "\x88" "\xe0" "\xa4" "\xb8" "\xe0" "\xa5" "\x87" " \xe0" "\xa4" "\xb9" "\xe0" "\xa5" "\x88" "\xe0" "\xa4" "\x82" "\xe0" "\xa5" "\xa4" "", 280 },
    { "\xe0" "\xa4" "\xa0" "\xe0" "\xa5" "\x80" "\xe0" "\xa4" "\x95" "", 0 },
};
static const seg_expect SPLIT_7[] = {
    { "\xd8" "\xa7" "\xd9" "\x84" "\xd8" "\xb3" "\xd9" "\x84" "\xd8" "\xa7" "\xd9" "\x85" " \xd8" "\xb9" "\xd9" "\x84" "\xdb" "\x8c" "\xda" "\xa9" "\xd9" "\x85" "\xdb" "\x94" "", 280 },
    { "\xd8" "\xa2" "\xd9" "\xbe" " \xda" "\xa9" "\xdb" "\x8c" "\xd8" "\xb3" "\xdb" "\x92" " \xdb" "\x81" "\xdb" "\x8c" "\xda" "\xba" "\xd8" "\x9f" "", 0 },
};
static const seg_expect SPLIT_8[] = {
    { "\xe4" "\xbd" "\xa0" "\xe5" "\xa5" "\xbd" "\xe3" "\x80" "\x82" "", 280 },
    { "\xe4" "\xbd" "\xa0" "\xe5" "\xa5" "\xbd" "\xe5" "\x90" "\x97" "\xef" "\xbc" "\x9f" "", 280 },
    { "\xe5" "\xbe" "\x88" "\xe5" "\xa5" "\xbd" "", 0 },
};
static const seg_expect SPLIT_9[] = {
    { "\xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" "\xe1" "\x8d" "\xa2" "", 280 },
    { "\xe1" "\x8a" "\xa5" "\xe1" "\x8a" "\x95" "\xe1" "\x8b" "\xb4" "\xe1" "\x89" "\xb5" " \xe1" "\x8a" "\x90" "\xe1" "\x88" "\x85" "", 0 },
};
static const seg_expect SPLIT_10[] = {
    { "\xe1" "\x9e" "\x9f" "\xe1" "\x9e" "\xbd" "\xe1" "\x9e" "\x9f" "\xe1" "\x9f" "\x92" "\xe1" "\x9e" "\x8f" "\xe1" "\x9e" "\xb8" "\xe1" "\x9f" "\x94" "", 280 },
    { "\xe1" "\x9e" "\xa2" "\xe1" "\x9f" "\x92" "\xe1" "\x9e" "\x93" "\xe1" "\x9e" "\x80" "\xe1" "\x9e" "\x9f" "\xe1" "\x9e" "\xbb" "\xe1" "\x9e" "\x81" "\xe1" "\x9e" "\x9f" "\xe1" "\x9e" "\x94" "\xe1" "\x9f" "\x92" "\xe1" "\x9e" "\x94" "\xe1" "\x9e" "\xb6" "\xe1" "\x9e" "\x99" "\xe1" "\x9e" "\x91" "\xe1" "\x9f" "\x81" "", 0 },
};
static const seg_expect SPLIT_11[] = {
    { "Line one", 400 },
    { "Line two.", 0 },
};
static const seg_expect SPLIT_12[] = {
    { "Wait.", 280 },
    { "Then go.", 0 },
};
static const seg_expect SPLIT_13[] = {
    { "He said \"go.", 280 },
    { "\" Then left.", 0 },
};
static const seg_expect SPLIT_14[] = {
    { "Hello.", 0 },
};
static const seg_expect SPLIT_15[] = {
    { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", 60 },
    { "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd tail.", 0 },
};
static const seg_expect SPLIT_16[] = {
    { "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", 60 },
    { "end.", 0 },
};

static const splitter_case SPLITTER_CASES[] = {
    { "empty", "", NULL, 0 },
    { "whitespace-only", "   \t  ", NULL, 0 },
    { "two-sentences", "Sawubona. Unjani?", SPLIT_2, 2 },
    { "clause-breaks", "Listen: this matters; then go. Done!", SPLIT_3, 4 },
    { "decimal-point", "It costs 3.5 rand. Really.", SPLIT_4, 2 },
    { "domain-name", "Visit thegeek.co.za for more. Thanks.", SPLIT_5, 2 },
    { "devanagari-danda", "\xe0" "\xa4" "\xa8" "\xe0" "\xa4" "\xae" "\xe0" "\xa4" "\xb8" "\xe0" "\xa5" "\x8d" "\xe0" "\xa4" "\xa4" "\xe0" "\xa5" "\x87" "\xe0" "\xa5" "\xa4" " \xe0" "\xa4" "\x86" "\xe0" "\xa4" "\xaa" " \xe0" "\xa4" "\x95" "\xe0" "\xa5" "\x88" "\xe0" "\xa4" "\xb8" "\xe0" "\xa5" "\x87" " \xe0" "\xa4" "\xb9" "\xe0" "\xa5" "\x88" "\xe0" "\xa4" "\x82" "\xe0" "\xa5" "\xa4" " \xe0" "\xa4" "\xa0" "\xe0" "\xa5" "\x80" "\xe0" "\xa4" "\x95" "", SPLIT_6, 3 },
    { "urdu-full-stop", "\xd8" "\xa7" "\xd9" "\x84" "\xd8" "\xb3" "\xd9" "\x84" "\xd8" "\xa7" "\xd9" "\x85" " \xd8" "\xb9" "\xd9" "\x84" "\xdb" "\x8c" "\xda" "\xa9" "\xd9" "\x85" "\xdb" "\x94" " \xd8" "\xa2" "\xd9" "\xbe" " \xda" "\xa9" "\xdb" "\x8c" "\xd8" "\xb3" "\xdb" "\x92" " \xdb" "\x81" "\xdb" "\x8c" "\xda" "\xba" "\xd8" "\x9f" "", SPLIT_7, 2 },
    { "cjk-no-space", "\xe4" "\xbd" "\xa0" "\xe5" "\xa5" "\xbd" "\xe3" "\x80" "\x82" "\xe4" "\xbd" "\xa0" "\xe5" "\xa5" "\xbd" "\xe5" "\x90" "\x97" "\xef" "\xbc" "\x9f" "\xe5" "\xbe" "\x88" "\xe5" "\xa5" "\xbd" "", SPLIT_8, 3 },
    { "ethiopic-stop", "\xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" "\xe1" "\x8d" "\xa2" " \xe1" "\x8a" "\xa5" "\xe1" "\x8a" "\x95" "\xe1" "\x8b" "\xb4" "\xe1" "\x89" "\xb5" " \xe1" "\x8a" "\x90" "\xe1" "\x88" "\x85" "", SPLIT_9, 2 },
    { "khmer-khan", "\xe1" "\x9e" "\x9f" "\xe1" "\x9e" "\xbd" "\xe1" "\x9e" "\x9f" "\xe1" "\x9f" "\x92" "\xe1" "\x9e" "\x8f" "\xe1" "\x9e" "\xb8" "\xe1" "\x9f" "\x94" " \xe1" "\x9e" "\xa2" "\xe1" "\x9f" "\x92" "\xe1" "\x9e" "\x93" "\xe1" "\x9e" "\x80" "\xe1" "\x9e" "\x9f" "\xe1" "\x9e" "\xbb" "\xe1" "\x9e" "\x81" "\xe1" "\x9e" "\x9f" "\xe1" "\x9e" "\x94" "\xe1" "\x9f" "\x92" "\xe1" "\x9e" "\x94" "\xe1" "\x9e" "\xb6" "\xe1" "\x9e" "\x99" "\xe1" "\x9e" "\x91" "\xe1" "\x9f" "\x81" "", SPLIT_10, 2 },
    { "paragraph-break", "Line one\nLine two.", SPLIT_11, 2 },
    { "ellipsis-absorbed", "Wait... Then go.", SPLIT_12, 2 },
    { "quote-absorbed", "He said \"go.\" Then left.", SPLIT_13, 2 },
    { "punctuation-only", "... Hello.", SPLIT_14, 1 },
    { "forced-cut", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd tail.", SPLIT_15, 2 },
    { "no-space-to-cut", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx end.", SPLIT_16, 2 },
};

static const span_expect SPANS_1[] = {
    { "Sawubona", 0 },
};
static const span_expect SPANS_2[] = {
    { "Igama lami ngu-", 0 },
    { "CircleAI", 1 },
};
static const span_expect SPANS_3[] = {
    { "Ngicela i-", 0 },
    { "GPS ", 1 },
    { "yakho, ngiyabonga", 0 },
};
static const span_expect SPANS_4[] = {
    { "WhatsApp ", 1 },
    { "iyasebenza kahle", 0 },
};
static const span_expect SPANS_5[] = {
    { "CircleAI ", 1 },
    { "ne-", 0 },
    { "YouTube", 1 },
};

static const spans_case SPANS_CASES[] = {
    { "", NULL, 0 },
    { "Sawubona", SPANS_1, 1 },
    { "Igama lami ngu-CircleAI", SPANS_2, 2 },
    { "Ngicela i-GPS yakho, ngiyabonga", SPANS_3, 3 },
    { "WhatsApp iyasebenza kahle", SPANS_4, 2 },
    { "CircleAI ne-YouTube", SPANS_5, 3 },
};

static const pair_case SPOKEN_CASES[] = {
    { "CircleAI", "Circle A.I." },
    { "YouTube", "You Tube" },
    { "OpenAPIKey", "Open A.P.I. Key" },
    { "GPS", "G.P.S." },
    { "Sawubona", "Sawubona" },
    { "A", "A" },
    { "", "" },
    { "iPhone", "i Phone" },
};

static const flag_case FOREIGN_CASES[] = {
    { "CircleAI", 1 },
    { "WhatsApp", 1 },
    { "GPS", 1 },
    { "SMS", 1 },
    { "ATM", 1 },
    { "Sawubona", 0 },
    { "hello", 0 },
    { "a", 0 },
    { "AB", 1 },
    { "ABCDEF", 0 },
    { "Ngiyabonga", 0 },
    { "iPhone", 1 },
};

static const flag_case ETHIOPIC_CASES[] = {
    { "", 0 },
    { "hello", 0 },
    { "\xe1" "\x8a" "\xa3" "\xe1" "\x88" "\x9b" "\xe1" "\x88" "\xad" "\xe1" "\x8a" "\x9b" "", 1 },
    { "abc \xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" "", 1 },
    { "123", 0 },
};

static const pair_case ROMANIZE_CASES[] = {
    { "", "" },
    { "\xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" "", "selam" },
    { "\xe1" "\x8a" "\xa0" "\xe1" "\x88" "\x9b" "\xe1" "\x88" "\xad" "\xe1" "\x8a" "\x9b" "", "amarnya" },
    { "\xe1" "\x8a" "\xa5" "\xe1" "\x8a" "\x95" "\xe1" "\x8a" "\xb3" "\xe1" "\x8a" "\x95" "", "enkwan" },
    { "\xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" "\xe1" "\x8d" "\xa2" " \xe1" "\x8a" "\xa5" "\xe1" "\x8a" "\x95" "\xe1" "\x8b" "\xb4" "\xe1" "\x89" "\xb5" " \xe1" "\x8a" "\x90" "\xe1" "\x88" "\x85" "", "selam. endet neh" },
    { "\xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" "\xe1" "\x8d" "\xa3" " \xe1" "\x8c" "\xa4" "\xe1" "\x8a" "\x93" " \xe1" "\x8b" "\xad" "\xe1" "\x88" "\xb5" "\xe1" "\x8c" "\xa5" "\xe1" "\x88" "\x8d" "\xe1" "\x8a" "\x9d" "", "selam, tena ystlny" },
    { "abc 123", "abc 123" },
    { "\xe1" "\x88" "\xb0" "\xe1" "\x88" "\x8b" "\xe1" "\x88" "\x9d" " abc", "selam abc" },
    { "\xe1" "\x8d" "\xa9" "\xe1" "\x8d" "\xaa" "\xe1" "\x8d" "\xab" "", "" },
    { "\xe1" "\x8d" "\x98" "\xe1" "\x8d" "\x99" "\xe1" "\x8d" "\x9a" "", "ryamyafya" },
    { "\xe1" "\x88" "\xb0" "\xe1" "\x8d" "\x9f" "\xe1" "\x88" "\x8b" "", "sela" },
};

#define TONE_WAVEFORM_TOLERANCE 9.9999999999999995e-07
#define TONE_COEFFICIENT_TOLERANCE 1.0000000000000001e-09
#define TONE_SAMPLE_RATE 22050
#define TONE_SILENCE_COUNT 8

static const settings_expect TONE_SETTINGS = { 320, 4, 3200, -4, 0.80000000000000004, 0.90000000000000002 };

static const coeff_case COEFF_CASES[] = {
    { 22050,
      { 1.0157038069473818, -1.8776854861325274, 0.87184849559871158 },
      { 1, -1.8795061241979216, 0.88573166448069895 },
      { 0.85846736091068476, -0.75493485386910197, 0.37450673044489252 },
      { 1, -0.75493485386910197, 0.23297409135557728 } },
    { 16000,
      { 1.0216323030920369, -1.8310210694606934, 0.82772865564498588 },
      { 1, -1.8344051701809314, 0.84597685801678468 },
      { 0.84204171276041229, -0.35350199262798726, 0.30191476556538394 },
      { 1, -0.35350199262798726, 0.14395647832579619 } },
    { 24000,
      { 1.0144288510935418, -1.8876877021992473, 0.88162656994155464 },
      { 1, -1.8892317247891599, 0.89451139844518435 },
      { 0.86383187292950214, -0.84447405792339991, 0.39821480360602618 },
      { 1, -0.84447405792339991, 0.26204667653552843 } },
};

static const double TONE_INPUT[] = {
    0, 0.21342043578624725, 0.32214230298995972, 0.29277178645133972,
    0.18552376329898834, 0.11220526695251465, 0.15876737236976624, 0.32566910982131958,
    0.52705222368240356, 0.64930242300033569, 0.62875187397003174, 0.49512147903442383,
    0.35084748268127441, 0.30050626397132874, 0.37884947657585144, 0.52691525220870972,
    0.63263565301895142, 0.60802763700485229, 0.44990789890289307, 0.24239680171012878,
    0.099484004080295563, 0.087128117680549622, 0.17842704057693481, 0.27266466617584229,
    0.26513341069221497, 0.11973145604133606, -0.10557492822408676, -0.29793515801429749,
    -0.36652201414108276, -0.30431139469146729, -0.19228045642375946, -0.14314846694469452,
    -0.22333912551403046, -0.40681198239326477, -0.59250468015670776, -0.67340445518493652,
    -0.60977882146835327, -0.45623651146888733, -0.32402154803276062, -0.3044607937335968,
    -0.40618374943733215, -0.54926693439483643, -0.62032562494277954, -0.55068457126617432,
    -0.36389267444610596, -0.15991875529289246, -0.046884588897228241, -0.067251712083816528,
    -0.16918881237506866, -0.24279163777828217, -0.19625045359134674, -0.019602119922637939,
    0.2077631950378418, 0.37101063132286072, 0.39814099669456482, 0.30884793400764465,
    0.20037949085235596, 0.180132195353508, 0.29131695628166199, 0.48279848694801331,
    0.64384579658508301, 0.67991471290588379, 0.57737869024276733, 0.41271978616714478,
};

static const double TONE_OUTPUT[] = {
    0, 0.17433382570743561, 0.24682421982288361, 0.2382454127073288,
    0.18960750102996826, 0.1631443202495575, 0.20869691669940948, 0.32927814126014709,
    0.47645056247711182, 0.58146995306015015, 0.60126310586929321, 0.54735714197158813,
    0.47755300998687744, 0.45589476823806763, 0.50839614868164062, 0.60495674610137939,
    0.67991471290588379, 0.67711955308914185, 0.58855134248733521, 0.45994231104850769,
    0.35953503847122192, 0.33144804835319519, 0.36569854617118835, 0.40548136830329895,
    0.38669434189796448, 0.28253611922264099, 0.12251269817352295, -0.027219628915190697,
    -0.10867349803447723, -0.11121615022420883, -0.0781979039311409, -0.076477959752082825,
    -0.14980451762676239, -0.2881036102771759, -0.43379566073417664, -0.52080816030502319,
    -0.5194774866104126, -0.4564858078956604, -0.39613577723503113, -0.39584088325500488,
    -0.46642860770225525, -0.56454694271087646, -0.62239468097686768, -0.59434264898300171,
    -0.48840042948722839, -0.36089813709259033, -0.27803683280944824, -0.27073034644126892,
    -0.31377327442169189, -0.3436470627784729, -0.30271753668785095, -0.17943717539310455,
    -0.01690959557890892, 0.11608016490936279, 0.1716674268245697, 0.15512116253376007,
    0.1205376610159874, 0.13298530876636505, 0.22313058376312256, 0.36554291844367981,
    0.49560016393661499, 0.55338376760482788, 0.52440071105957031, 0.44914954900741577,
};

static const char *const NCHLT_DICT =
    "sawubona\ts a w u b O n a" "\n"
    "sawubona\ts a w u b o n a" "\n"
    "banga\tb a N a" "\n"
    "\tnot a word" "\n"
    "novalue\t"
    ;

static const char *const NCHLT_RULES =
    "a;;;1;0;100" "\n"
    "b;;;2;0;100" "\n"
    "n;;;3;0;100" "\n"
    "n;;g;4;2;40" "\n"
    "g;;;5;0;100" "\n"
    "g;n;;0;2;40" "\n"
    "bad line without semicolons" "\n"
    "x;;;;9;0"
    ;

static const char *const NCHLT_PHONE_MAP =
    "1\ta" "\n"
    "2\tb" "\n"
    "3\tn" "\n"
    "4\tN" "\n"
    "5\tg" "\n"
    "toolong\tz"
    ;

static const char *const NCHLT_GRAPH_MAP =
    "b\tq"
    ;

static const char *const NCHLT_GNULLS =
    "bb;b"
    ;

static const char *const NPH_0[] = { "s", "a", "w", "u", "b", "O", "n", "a" };
static const char *const NPH_1[] = { "g", "a", "b", "a" };
static const char *const NPH_2[] = { "N", "a", "N", "a" };
static const char *const NPH_3[] = { "b", "a", "N", "a" };
static const char *const NPH_4[] = { "a", "b", "a" };
static const char *const NPH_5[] = { "a", "b" };
static const char *const NUN_5[] = { "z" };
static const char *const NPH_6[] = { "s", "a", "w", "u", "b", "O", "n", "a", "g", "a", "b", "a" };

static const nchlt_case NCHLT_CASES[] = {
    { "dictionary-hit", "sawubona", NPH_0, 8, 0, NULL, 0 },
    { "rule-predicted", "gaba", NPH_1, 4, 1, NULL, 0 },
    { "context-rule", "nganga", NPH_2, 4, 1, NULL, 0 },
    { "grapheme-remap", "qanga", NPH_3, 4, 1, NULL, 0 },
    { "gnull-collapse", "abba", NPH_4, 3, 1, NULL, 0 },
    { "unknown-grapheme", "azb", NPH_5, 2, 1, NUN_5, 1 },
    { "mixed-sentence", "Sawubona, gaba!", NPH_6, 12, 1, NULL, 0 },
    { "empty", "", NULL, 0, 0, NULL, 0 },
    { "punctuation-only", "!!! ...", NULL, 0, 0, NULL, 0 },
};

static const char *const PRED_0[] = { "b", "a", "N", "a" };
static const char *const PRED_1[] = { "g", "a", "b", "a" };
static const char *const PRED_2[] = { "N", "a", "N", "a" };
static const char *const PRED_3[] = { "a", "b" };

static const predict_case PREDICT_CASES[] = {
    { "banga", PRED_0, 4 },
    { "gaba", PRED_1, 4 },
    { "nganga", PRED_2, 4 },
    { "azb", PRED_3, 2 },
    { "", NULL, 0 },
};
