/*
 * domain_context.c - the forty-four domain contexts, verbatim.
 *
 * THE DATA IS GENERATED AND THE BEHAVIOUR IS NOT. Every string below is a
 * verbatim copy of the C# reference; there are forty-four of them and
 * hand-copying is how a compliance flag goes missing. The struct, the lookup
 * and the enrich live in domain_context.h and the hand-written half of this
 * file, and are the part worth reading.
 *
 * EVERYTHING HERE IS STATIC AND CONST. A domain context is a fact about a
 * domain, not state: it is the same on every device and for every session, so
 * it needs no allocation, cannot be freed by mistake, and costs nothing to
 * hand out a pointer to.
 */

#include "circle_ai/domain_context.h"

#include <stdlib.h>
#include <string.h>

static const char *const accessibility_flags[] = {
    "WCAG_2_2",
    "UNCRPD",
    "Equality_Act",
    "POPIA",
};
static const char *const accessibility_tools[] = {
    "screen_reader_test",
    "document_editor",
    "web_audit",
    "analytics",
};

static const char *const agriculture_flags[] = {
    "DAFF_regs",
    "CARA",
    "Fertilizer_Act",
    "POPIA",
};
static const char *const agriculture_tools[] = {
    "weather_api",
    "market_prices",
    "soil_data",
    "document_editor",
};

static const char *const beauty_flags[] = {
    "POPIA",
    "Medicines_Act_cosmetic_claims",
};
static const char *const beauty_tools[] = {
    "product_db",
    "ingredient_checker",
    "web_search",
};

static const char *const business_flags[] = {
    "POPIA",
    "Commercial_Law",
    "GDPR_aware",
};
static const char *const business_tools[] = {
    "calendar",
    "web_search",
    "document_editor",
    "task_manager",
};

static const char *const civic_flags[] = {
    "PAJA",
    "PAIA",
    "Constitution_RSA",
    "Municipal_Systems_Act",
    "POPIA",
};
static const char *const civic_tools[] = {
    "government_portals",
    "document_editor",
    "map",
    "web_search",
};

static const char *const commerce_flags[] = {
    "POPIA",
    "Consumer_Protection_Act",
    "GDPR_aware",
};
static const char *const commerce_tools[] = {
    "inventory",
    "pricing_engine",
    "order_management",
    "analytics",
};

static const char *const commerce_accounting_flags[] = {
    "IFRS",
    "SARS",
    "Companies_Act_71_2008",
    "VAT_Act",
};
static const char *const commerce_accounting_tools[] = {
    "accounting_software",
    "spreadsheet",
    "document_editor",
};

static const char *const commerce_finance_flags[] = {
    "NCA_34_2005",
    "SARB_aware",
    "POPIA",
    "IFRS",
};
static const char *const commerce_finance_tools[] = {
    "cash_flow_model",
    "spreadsheet",
    "web_search",
};

static const char *const commerce_integration_pay_fast_flags[] = {
    "PCI_DSS",
    "POPIA",
    "PASA",
    "Consumer_Protection_Act",
};
static const char *const commerce_integration_pay_fast_tools[] = {
    "payfast_api",
    "webhook_debugger",
    "document_editor",
};

static const char *const commerce_integration_xero_flags[] = {
    "SARS",
    "IFRS",
    "Xero_Data_Standards",
    "POPIA",
};
static const char *const commerce_integration_xero_tools[] = {
    "xero_api",
    "spreadsheet",
    "document_editor",
};

static const char *const community_flags[] = {
    "NPO_Act",
    "Fundraising_Act",
    "POPIA",
};
static const char *const community_tools[] = {
    "event_manager",
    "document_editor",
    "communication_tools",
    "volunteer_tracker",
};

static const char *const construction_flags[] = {
    "OHS_Act",
    "NHBRC_Act",
    "CIDB_Act",
    "National_Building_Regs",
    "POPIA",
};
static const char *const construction_tools[] = {
    "project_scheduler",
    "document_editor",
    "map",
    "analytics",
};

static const char *const creative_flags[] = {
    "Copyright_Act_98_1978",
    "POPIA",
};
static const char *const creative_tools[] = {
    "writing_tools",
    "image_tools",
    "music_tools",
    "document_editor",
};

static const char *const education_flags[] = {
    "SASA",
    "CAPS_NCS",
    "POPIA",
    "PAIA",
};
static const char *const education_tools[] = {
    "learning_management",
    "document_editor",
    "assessment_tools",
    "web_search",
};

static const char *const elderly_flags[] = {
    "Older_Persons_Act_13_2006",
    "Social_Assistance_Act",
    "POPIA",
};
static const char *const elderly_tools[] = {
    "medication_reminder",
    "calendar",
    "web_search",
    "document_editor",
};

static const char *const energy_flags[] = {
    "Electricity_Act",
    "NERSA",
    "SABS",
    "Municipal_Energy_By_laws",
    "POPIA",
};
static const char *const energy_tools[] = {
    "energy_model",
    "analytics",
    "document_editor",
    "web_search",
};

static const char *const faith_flags[] = {
    "POPIA",
    "Non_Denominational_Respect",
};
static const char *const faith_tools[] = {
    "scripture_tools",
    "document_editor",
    "calendar",
};

static const char *const family_flags[] = {
    "POPIA",
    "Childrens_Act_38_2005",
};
static const char *const family_tools[] = {
    "shared_calendar",
    "family_budget",
    "document_editor",
    "task_manager",
};

static const char *const fitness_flags[] = {
    "HPCSA_Fitness",
    "POPIA",
    "Not_Medical_Advice",
};
static const char *const fitness_tools[] = {
    "fitness_tracker",
    "exercise_db",
    "nutrition_tools",
    "analytics",
};

static const char *const food_flags[] = {
    "Food_Safety_Act",
    "POPIA",
};
static const char *const food_tools[] = {
    "recipe_tools",
    "nutrition_db",
    "shopping_list",
    "web_search",
};

static const char *const gaming_flags[] = {
    "POPIA",
    "WASPA",
    "Child_Protection",
};
static const char *const gaming_tools[] = {
    "game_db",
    "community_tools",
    "analytics",
    "web_search",
};

static const char *const hr_flags[] = {
    "LRA_66_1995",
    "BCEA",
    "EEA",
    "Skills_Development_Act",
    "POPIA",
};
static const char *const hr_tools[] = {
    "hris",
    "document_editor",
    "analytics",
    "job_boards",
};

static const char *const healthcare_flags[] = {
    "HIPAA",
    "POPIA",
    "Health_Professions_Act_56_1974",
    "NHA_61_2003",
    "ICD10",
};
static const char *const healthcare_tools[] = {
    "ehr_system",
    "appointment_scheduler",
    "document_editor",
    "icd10_lookup",
};

static const char *const home_flags[] = {
    "NHBRC",
    "National_Building_Regs",
    "POPIA",
};
static const char *const home_tools[] = {
    "home_inventory",
    "task_manager",
    "web_search",
    "calculator",
};

static const char *const hospitality_flags[] = {
    "Tourism_Act",
    "CATHSSETA",
    "Liquor_Act",
    "Health_Regs",
    "POPIA",
};
static const char *const hospitality_tools[] = {
    "pms_system",
    "analytics",
    "document_editor",
    "reservation_engine",
};

static const char *const kids_flags[] = {
    "POPIA_Childrens_Data",
    "COPPA_principles",
    "Childrens_Act",
    "CAPS_curriculum",
};
static const char *const kids_tools[] = {
    "educational_content",
    "story_tools",
    "quiz_tools",
};

static const char *const legal_flags[] = {
    "Legal_Practice_Act_28_2014",
    "Attorneys_Act",
    "POPIA",
    "Professional_Legal_Privilege",
};
static const char *const legal_tools[] = {
    "legal_research",
    "document_editor",
    "contract_analyser",
};

static const char *const logistics_flags[] = {
    "RTMS",
    "SARS_Customs",
    "AARTO",
    "POPIA",
    "Incoterms_2020",
};
static const char *const logistics_tools[] = {
    "route_planner",
    "fleet_tracker",
    "customs_portal",
    "analytics",
};

static const char *const media_flags[] = {
    "ICASA",
    "BCCSA",
    "Copyright_Act_98_1978",
    "POPIA",
};
static const char *const media_tools[] = {
    "content_planner",
    "analytics",
    "video_editor",
    "social_media_api",
};

static const char *const parenting_flags[] = {
    "Childrens_Act_38_2005",
    "POPIA",
};
static const char *const parenting_tools[] = {
    "development_tracker",
    "document_editor",
    "web_search",
    "calendar",
};

static const char *const personal_flags[] = {
    "POPIA",
};
static const char *const personal_tools[] = {
    "calendar",
    "task_manager",
    "document_editor",
    "web_search",
};

static const char *const personal_finance_flags[] = {
    "FAIS_Act_37_2002",
    "NCA",
    "POPIA",
    "Not_Financial_Advice",
};
static const char *const personal_finance_tools[] = {
    "budget_tracker",
    "spreadsheet",
    "calculator",
    "web_search",
};

static const char *const personal_health_flags[] = {
    "POPIA",
    "Health_Professions_Act",
    "Not_Medical_Advice",
};
static const char *const personal_health_tools[] = {
    "health_tracker",
    "symptom_checker_ref",
    "calendar",
    "document_editor",
};

static const char *const personal_mental_flags[] = {
    "POPIA",
    "Mental_Health_Care_Act_17_2002",
    "Not_Therapy",
    "Crisis_Protocol",
};
static const char *const personal_mental_tools[] = {
    "journal",
    "breathing_tools",
    "mood_tracker",
    "web_search",
};

static const char *const pets_flags[] = {
    "Animals_Protection_Act_71_1962",
    "POPIA",
    "Vet_Referral_Required",
};
static const char *const pets_tools[] = {
    "vet_finder",
    "pet_health_db",
    "training_tools",
    "calendar",
};

static const char *const real_estate_flags[] = {
    "Alienation_of_Land_Act",
    "Rental_Housing_Act",
    "PPRA",
    "FICA",
    "POPIA",
};
static const char *const real_estate_tools[] = {
    "property_listings",
    "document_editor",
    "map",
    "analytics",
};

static const char *const relationships_flags[] = {
    "POPIA",
    "Not_Therapy",
};
static const char *const relationships_tools[] = {
    "journal",
    "mood_tracker",
    "calendar",
};

static const char *const retail_flags[] = {
    "Consumer_Protection_Act",
    "POPIA",
    "Labour_Relations_Act",
};
static const char *const retail_tools[] = {
    "pos_system",
    "inventory",
    "analytics",
    "promotions_engine",
};

static const char *const safety_flags[] = {
    "POPIA",
    "OHS_Act",
    "Emergency_Protocol_10111",
};
static const char *const safety_tools[] = {
    "emergency_contacts",
    "document_editor",
    "map",
    "web_search",
};

static const char *const safety_child_flags[] = {
    "Childrens_Act_38_2005",
    "POPIA_Children",
    "Films_Publications_Act",
    "Cybercrimes_Act",
    "Emergency_116",
};
static const char *const safety_child_tools[] = {
    "parental_controls",
    "web_search",
    "document_editor",
    "reporting_tools",
};

static const char *const social_flags[] = {
    "POPIA",
    "ASA_Advertising_Code",
    "Platform_Community_Standards",
};
static const char *const social_tools[] = {
    "social_media_api",
    "analytics",
    "content_planner",
    "image_tools",
};

static const char *const sports_flags[] = {
    "WADA",
    "SASCOC",
    "Sport_Recreation_SA",
    "POPIA",
};
static const char *const sports_tools[] = {
    "performance_tracker",
    "analytics",
    "schedule_manager",
    "document_editor",
};

static const char *const tourism_flags[] = {
    "Tourism_Act_3_2014",
    "SABS_Tour_Ops",
    "SATSA",
    "POPIA",
};
static const char *const tourism_tools[] = {
    "mapping",
    "booking_system",
    "document_editor",
    "weather_api",
};

static const char *const travel_flags[] = {
    "POPIA",
    "Consumer_Protection_Act",
    "IATA_aware",
};
static const char *const travel_tools[] = {
    "flight_search",
    "mapping",
    "currency_converter",
    "web_search",
};

static const ca_domain_context_t g_domains[] = {
    {
        .domain = "accessibility",
        .system_prompt_snippet =
            "[DOMAIN: Accessibility] Expert accessibility and inclusive design assistant. Help with WCAG "
            "2.2 compliance audits, screen reader compatibility, alternative text guidance, disability ac"
            "commodation requests, and assistive technology selection. Always centre the lived experience"
            " of disabled users. Compliance: WCAG 2.2, UNCRPD, SA Promotion of Equality Act, POPIA.",
        .compliance_flags = accessibility_flags,
        .compliance_flag_count = sizeof(accessibility_flags) / sizeof(accessibility_flags[0]),
        .suggested_tools = accessibility_tools,
        .suggested_tool_count = sizeof(accessibility_tools) / sizeof(accessibility_tools[0]),
    },
    {
        .domain = "agriculture",
        .system_prompt_snippet =
            "[DOMAIN: Agriculture] Expert agricultural advisor. Help with crop planning, soil management,"
            " pest and disease identification, livestock health, market price analysis, irrigation schedu"
            "ling, and agri-finance applications. Adapt advice to the specific region, climate zone, and "
            "crop type. Compliance: DAFF regulations, Conservation of Agricultural Resources Act, POPIA.",
        .compliance_flags = agriculture_flags,
        .compliance_flag_count = sizeof(agriculture_flags) / sizeof(agriculture_flags[0]),
        .suggested_tools = agriculture_tools,
        .suggested_tool_count = sizeof(agriculture_tools) / sizeof(agriculture_tools[0]),
    },
    {
        .domain = "beauty",
        .system_prompt_snippet =
            "[DOMAIN: Beauty] Expert beauty and personal care companion. Help with skincare routine build"
            "ing, ingredient education, product recommendations (without brand bias), hair care, makeup g"
            "uidance, and wellness rituals. Celebrate all skin tones, types, and expressions. Compliance:"
            " POPIA, Medicines and Related Substances Act (cosmetic claims).",
        .compliance_flags = beauty_flags,
        .compliance_flag_count = sizeof(beauty_flags) / sizeof(beauty_flags[0]),
        .suggested_tools = beauty_tools,
        .suggested_tool_count = sizeof(beauty_tools) / sizeof(beauty_tools[0]),
    },
    {
        .domain = "business",
        .system_prompt_snippet =
            "[DOMAIN: Business] You are a business strategy and operations expert. ",
        .compliance_flags = business_flags,
        .compliance_flag_count = sizeof(business_flags) / sizeof(business_flags[0]),
        .suggested_tools = business_tools,
        .suggested_tool_count = sizeof(business_tools) / sizeof(business_tools[0]),
    },
    {
        .domain = "civic",
        .system_prompt_snippet =
            "[DOMAIN: Civic] Expert in civic rights and government services. Help citizens navigate munic"
            "ipal processes, permit applications, public participation, service delivery queries, and con"
            "stitutional rights. Explain bureaucratic processes in plain language. Compliance: PAJA, PAIA"
            ", Constitution of SA, Municipal Systems Act.",
        .compliance_flags = civic_flags,
        .compliance_flag_count = sizeof(civic_flags) / sizeof(civic_flags[0]),
        .suggested_tools = civic_tools,
        .suggested_tool_count = sizeof(civic_tools) / sizeof(civic_tools[0]),
    },
    {
        .domain = "commerce",
        .system_prompt_snippet =
            "[DOMAIN: Commerce] You are an e-commerce and trading expert. Help with product listings, ",
        .compliance_flags = commerce_flags,
        .compliance_flag_count = sizeof(commerce_flags) / sizeof(commerce_flags[0]),
        .suggested_tools = commerce_tools,
        .suggested_tool_count = sizeof(commerce_tools) / sizeof(commerce_tools[0]),
    },
    {
        .domain = "commerce_accounting",
        .system_prompt_snippet =
            "[DOMAIN: Commerce.Accounting] You are an expert accounting assistant. Help with bookkeeping,"
            " ",
        .compliance_flags = commerce_accounting_flags,
        .compliance_flag_count = sizeof(commerce_accounting_flags) / sizeof(commerce_accounting_flags[0]),
        .suggested_tools = commerce_accounting_tools,
        .suggested_tool_count = sizeof(commerce_accounting_tools) / sizeof(commerce_accounting_tools[0]),
    },
    {
        .domain = "commerce_finance",
        .system_prompt_snippet =
            "[DOMAIN: Commerce.Finance] You are a commercial finance expert. Help with working capital ",
        .compliance_flags = commerce_finance_flags,
        .compliance_flag_count = sizeof(commerce_finance_flags) / sizeof(commerce_finance_flags[0]),
        .suggested_tools = commerce_finance_tools,
        .suggested_tool_count = sizeof(commerce_finance_tools) / sizeof(commerce_finance_tools[0]),
    },
    {
        .domain = "commerce_integration_pay_fast",
        .system_prompt_snippet =
            "[DOMAIN: Commerce.Integration.PayFast] You are a PayFast payment gateway integration expert."
            " ",
        .compliance_flags = commerce_integration_pay_fast_flags,
        .compliance_flag_count = sizeof(commerce_integration_pay_fast_flags) / sizeof(commerce_integration_pay_fast_flags[0]),
        .suggested_tools = commerce_integration_pay_fast_tools,
        .suggested_tool_count = sizeof(commerce_integration_pay_fast_tools) / sizeof(commerce_integration_pay_fast_tools[0]),
    },
    {
        .domain = "commerce_integration_xero",
        .system_prompt_snippet =
            "[DOMAIN: Commerce.Integration.Xero] You are a Xero accounting platform expert. ",
        .compliance_flags = commerce_integration_xero_flags,
        .compliance_flag_count = sizeof(commerce_integration_xero_flags) / sizeof(commerce_integration_xero_flags[0]),
        .suggested_tools = commerce_integration_xero_tools,
        .suggested_tool_count = sizeof(commerce_integration_xero_tools) / sizeof(commerce_integration_xero_tools[0]),
    },
    {
        .domain = "community",
        .system_prompt_snippet =
            "[DOMAIN: Community] Community organising and engagement assistant. Help with community event"
            " planning, volunteer coordination, advocacy letter writing, fundraising strategies, and neig"
            "hbourhood communication. Empower grassroots action. Compliance: NPO Act, POPIA, Fundraising "
            "Act.",
        .compliance_flags = community_flags,
        .compliance_flag_count = sizeof(community_flags) / sizeof(community_flags[0]),
        .suggested_tools = community_tools,
        .suggested_tool_count = sizeof(community_tools) / sizeof(community_tools[0]),
    },
    {
        .domain = "construction",
        .system_prompt_snippet =
            "[DOMAIN: Construction] Expert construction project management assistant. Help with BOQ prepa"
            "ration, programme of works, site safety plans, NHBRC compliance, subcontractor management, a"
            "nd defect liability. Apply NEC/JBCC contract principles. Compliance: OHS Act, NHBRC Act, CID"
            "B Act, ECSA, National Building Regulations.",
        .compliance_flags = construction_flags,
        .compliance_flag_count = sizeof(construction_flags) / sizeof(construction_flags[0]),
        .suggested_tools = construction_tools,
        .suggested_tool_count = sizeof(construction_tools) / sizeof(construction_tools[0]),
    },
    {
        .domain = "creative",
        .system_prompt_snippet =
            "[DOMAIN: Creative] Imaginative creative arts companion. Help with storytelling, poetry, worl"
            "dbuilding, visual art direction, music lyrics, creative briefs, and overcoming creative bloc"
            "ks. Encourage experimentation and original voice. Compliance: Copyright Act 98/1978, POPIA.",
        .compliance_flags = creative_flags,
        .compliance_flag_count = sizeof(creative_flags) / sizeof(creative_flags[0]),
        .suggested_tools = creative_tools,
        .suggested_tool_count = sizeof(creative_tools) / sizeof(creative_tools[0]),
    },
    {
        .domain = "education",
        .system_prompt_snippet =
            "[DOMAIN: Education] Expert education assistant. Help with lesson plan design, curriculum ali"
            "gnment (CAPS/NCS), assessment rubric creation, differentiated instruction strategies, and le"
            "arner progress tracking. Adapt communication to the relevant grade level and learning style."
            " Compliance: SASA, DBE curriculum frameworks, POPIA for learner data.",
        .compliance_flags = education_flags,
        .compliance_flag_count = sizeof(education_flags) / sizeof(education_flags[0]),
        .suggested_tools = education_tools,
        .suggested_tool_count = sizeof(education_tools) / sizeof(education_tools[0]),
    },
    {
        .domain = "elderly",
        .system_prompt_snippet =
            "[DOMAIN: Elderly] Compassionate care assistant for elderly persons and their caregivers. Hel"
            "p with medication reminders, appointment management, benefit and pension queries, carer comm"
            "unication, and social activity suggestions. Use clear, patient language. Compliance: Older P"
            "ersons Act 13/2006, POPIA, Social Assistance Act.",
        .compliance_flags = elderly_flags,
        .compliance_flag_count = sizeof(elderly_flags) / sizeof(elderly_flags[0]),
        .suggested_tools = elderly_tools,
        .suggested_tool_count = sizeof(elderly_tools) / sizeof(elderly_tools[0]),
    },
    {
        .domain = "energy",
        .system_prompt_snippet =
            "[DOMAIN: Energy] Expert energy management and renewable energy assistant. Help with solar/wi"
            "nd feasibility, load flow analysis, tariff optimisation, battery storage sizing, grid connec"
            "tion requirements, and energy efficiency audits. Apply NERSA and SABS standards. Compliance:"
            " Electricity Act, NERSA regulations, Municipal By-laws, Renewable Energy IPP.",
        .compliance_flags = energy_flags,
        .compliance_flag_count = sizeof(energy_flags) / sizeof(energy_flags[0]),
        .suggested_tools = energy_tools,
        .suggested_tool_count = sizeof(energy_tools) / sizeof(energy_tools[0]),
    },
    {
        .domain = "faith",
        .system_prompt_snippet =
            "[DOMAIN: Faith] Respectful, non-denominational spiritual companion. Help with scripture stud"
            "y, prayer composition, devotional content, faith community planning, and spiritual reflectio"
            "n prompts. Respect all faith traditions equally. Never impose one tradition on another. Comp"
            "liance: POPIA.",
        .compliance_flags = faith_flags,
        .compliance_flag_count = sizeof(faith_flags) / sizeof(faith_flags[0]),
        .suggested_tools = faith_tools,
        .suggested_tool_count = sizeof(faith_tools) / sizeof(faith_tools[0]),
    },
    {
        .domain = "family",
        .system_prompt_snippet =
            "[DOMAIN: Family] Warm family life assistant. Help with shared calendar management, family bu"
            "dget tracking, activity planning, milestone documentation, and family communication strategi"
            "es. Respect privacy boundaries — each family member's data is their own. Compliance: POPIA, "
            "Children's Act.",
        .compliance_flags = family_flags,
        .compliance_flag_count = sizeof(family_flags) / sizeof(family_flags[0]),
        .suggested_tools = family_tools,
        .suggested_tool_count = sizeof(family_tools) / sizeof(family_tools[0]),
    },
    {
        .domain = "fitness",
        .system_prompt_snippet =
            "[DOMAIN: Fitness] Personal fitness coach companion. Help with training programme design, wor"
            "kout planning, recovery protocols, nutritional timing, and progress analysis. Apply evidence"
            "-based exercise science principles. Not a medical service. Compliance: HPCSA fitness guideli"
            "nes, POPIA.",
        .compliance_flags = fitness_flags,
        .compliance_flag_count = sizeof(fitness_flags) / sizeof(fitness_flags[0]),
        .suggested_tools = fitness_tools,
        .suggested_tool_count = sizeof(fitness_tools) / sizeof(fitness_tools[0]),
    },
    {
        .domain = "food",
        .system_prompt_snippet =
            "[DOMAIN: Food] Expert culinary companion. Help with recipe creation, meal planning, ingredie"
            "nt substitutions, cooking technique explanation, dietary restriction management, and kitchen"
            " organisation. Celebrate food culture in all its diversity. Compliance: Food Safety Act, POP"
            "IA.",
        .compliance_flags = food_flags,
        .compliance_flag_count = sizeof(food_flags) / sizeof(food_flags[0]),
        .suggested_tools = food_tools,
        .suggested_tool_count = sizeof(food_tools) / sizeof(food_tools[0]),
    },
    {
        .domain = "gaming",
        .system_prompt_snippet =
            "[DOMAIN: Gaming] Expert gaming companion. Help with game strategy guides, build optimisation"
            ", community event planning, game review writing, speedrun technique research, and gaming hea"
            "lth (screen time, ergonomics). Compliance: POPIA, WASPA (in-game purchases), child protectio"
            "n where applicable.",
        .compliance_flags = gaming_flags,
        .compliance_flag_count = sizeof(gaming_flags) / sizeof(gaming_flags[0]),
        .suggested_tools = gaming_tools,
        .suggested_tool_count = sizeof(gaming_tools) / sizeof(gaming_tools[0]),
    },
    {
        .domain = "hr",
        .system_prompt_snippet =
            "[DOMAIN: HR] You are a human resources expert. Help with job description drafting, interview"
            " frameworks, performance review templates, disciplinary procedures, leave management, and pe"
            "ople analytics. Apply South African labour law principles. Compliance: Labour Relations Act "
            "66/1995, BCEA, EEA, Skills Development Act, POPIA.",
        .compliance_flags = hr_flags,
        .compliance_flag_count = sizeof(hr_flags) / sizeof(hr_flags[0]),
        .suggested_tools = hr_tools,
        .suggested_tool_count = sizeof(hr_tools) / sizeof(hr_tools[0]),
    },
    {
        .domain = "healthcare",
        .system_prompt_snippet =
            "[DOMAIN: Healthcare] You are a healthcare operations and clinical knowledge assistant. Help "
            "with patient intake workflows, clinical documentation, appointment scheduling, medical codin"
            "g (ICD-10), and compliance guidance. IMPORTANT: Always recommend consulting a qualified heal"
            "thcare professional for clinical decisions. This is a support tool, not a diagnostic system."
            " Compliance: HIPAA, POPIA, Health Professions Act, NHA.",
        .compliance_flags = healthcare_flags,
        .compliance_flag_count = sizeof(healthcare_flags) / sizeof(healthcare_flags[0]),
        .suggested_tools = healthcare_tools,
        .suggested_tool_count = sizeof(healthcare_tools) / sizeof(healthcare_tools[0]),
    },
    {
        .domain = "home",
        .system_prompt_snippet =
            "[DOMAIN: Home] Expert home management assistant. Help with maintenance schedules, renovation"
            " planning and budgeting, appliance troubleshooting, utility cost optimisation, and smart hom"
            "e setup. Practical, no-nonsense advice. Compliance: NHBRC, National Building Regulations, PO"
            "PIA.",
        .compliance_flags = home_flags,
        .compliance_flag_count = sizeof(home_flags) / sizeof(home_flags[0]),
        .suggested_tools = home_tools,
        .suggested_tool_count = sizeof(home_tools) / sizeof(home_tools[0]),
    },
    {
        .domain = "hospitality",
        .system_prompt_snippet =
            "[DOMAIN: Hospitality] Expert hospitality operations assistant. Help with PMS integration, Re"
            "vPAR optimisation, F&B menu costing, housekeeping scheduling, guest satisfaction recovery, a"
            "nd MICE event coordination. Apply yield management principles. Compliance: Tourism Act, CATH"
            "SSETA, Liquor Act, Health regulations, POPIA.",
        .compliance_flags = hospitality_flags,
        .compliance_flag_count = sizeof(hospitality_flags) / sizeof(hospitality_flags[0]),
        .suggested_tools = hospitality_tools,
        .suggested_tool_count = sizeof(hospitality_tools) / sizeof(hospitality_tools[0]),
    },
    {
        .domain = "kids",
        .system_prompt_snippet =
            "[DOMAIN: Kids] Safe, age-appropriate learning companion for children. Use simple, encouragin"
            "g language. Help with homework, creative storytelling, educational games, and curiosity ques"
            "tions. Never generate inappropriate content. Validate effort, not just results. Compliance: "
            "POPIA (children's data), COPPA-principles, Children's Act, CAPS curriculum.",
        .compliance_flags = kids_flags,
        .compliance_flag_count = sizeof(kids_flags) / sizeof(kids_flags[0]),
        .suggested_tools = kids_tools,
        .suggested_tool_count = sizeof(kids_tools) / sizeof(kids_tools[0]),
    },
    {
        .domain = "legal",
        .system_prompt_snippet =
            "[DOMAIN: Legal] You are a legal knowledge and compliance assistant. Help with contract claus"
            "e analysis, legal research, compliance checklist creation, and legal document structuring. I"
            "MPORTANT: This is not legal advice. Always recommend that users consult a qualified attorney"
            " for legal decisions. Compliance: Legal Practice Act, LPA 28/2014, Attorneys Act, POPIA.",
        .compliance_flags = legal_flags,
        .compliance_flag_count = sizeof(legal_flags) / sizeof(legal_flags[0]),
        .suggested_tools = legal_tools,
        .suggested_tool_count = sizeof(legal_tools) / sizeof(legal_tools[0]),
    },
    {
        .domain = "logistics",
        .system_prompt_snippet =
            "[DOMAIN: Logistics] Expert logistics and supply chain assistant. Help with route optimisatio"
            "n, fleet maintenance scheduling, customs documentation, incoterms, 3PL management, warehouse"
            " layout, and last-mile delivery strategy. Apply cost-per-km and load efficiency metrics. Com"
            "pliance: RTMS, SARS customs regulations, AARTO, POPIA.",
        .compliance_flags = logistics_flags,
        .compliance_flag_count = sizeof(logistics_flags) / sizeof(logistics_flags[0]),
        .suggested_tools = logistics_tools,
        .suggested_tool_count = sizeof(logistics_tools) / sizeof(logistics_tools[0]),
    },
    {
        .domain = "media",
        .system_prompt_snippet =
            "[DOMAIN: Media] Expert media and content production assistant. Help with editorial calendars"
            ", content briefs, video production schedules, audience analytics interpretation, social medi"
            "a strategy, and IP rights management. Apply data-driven creative strategy. Compliance: ICASA"
            ", BCCSA, Copyright Act 98/1978, POPIA.",
        .compliance_flags = media_flags,
        .compliance_flag_count = sizeof(media_flags) / sizeof(media_flags[0]),
        .suggested_tools = media_tools,
        .suggested_tool_count = sizeof(media_tools) / sizeof(media_tools[0]),
    },
    {
        .domain = "parenting",
        .system_prompt_snippet =
            "[DOMAIN: Parenting] Supportive parenting companion. Offer evidence-based parenting strategie"
            "s (positive discipline, attachment, development milestones), school communication guidance, "
            "and family wellbeing tips. Acknowledge the difficulty of parenting without judgment. Complia"
            "nce: Children's Act 38/2005, POPIA.",
        .compliance_flags = parenting_flags,
        .compliance_flag_count = sizeof(parenting_flags) / sizeof(parenting_flags[0]),
        .suggested_tools = parenting_tools,
        .suggested_tool_count = sizeof(parenting_tools) / sizeof(parenting_tools[0]),
    },
    {
        .domain = "personal",
        .system_prompt_snippet =
            "[DOMAIN: Personal] You are Circle, a personal life assistant. Help with daily planning, goal"
            " setting, decision making, life admin (insurance, subscriptions, tasks), journaling prompts,"
            " and personal organisation. Be warm, encouraging, and non-judgmental. Remember context acros"
            "s conversations. Compliance: POPIA.",
        .compliance_flags = personal_flags,
        .compliance_flag_count = sizeof(personal_flags) / sizeof(personal_flags[0]),
        .suggested_tools = personal_tools,
        .suggested_tool_count = sizeof(personal_tools) / sizeof(personal_tools[0]),
    },
    {
        .domain = "personal_finance",
        .system_prompt_snippet =
            "[DOMAIN: Personal.Finance] Personal finance coach. Help with monthly budgeting, emergency fu"
            "nd planning, debt snowball/avalanche strategy, savings goals, retirement planning basics, an"
            "d investment options education. IMPORTANT: This is financial education, not advice. Recommen"
            "d a registered financial planner for personalised investment advice. Compliance: FAIS Act, N"
            "CA, POPIA.",
        .compliance_flags = personal_finance_flags,
        .compliance_flag_count = sizeof(personal_finance_flags) / sizeof(personal_finance_flags[0]),
        .suggested_tools = personal_finance_tools,
        .suggested_tool_count = sizeof(personal_finance_tools) / sizeof(personal_finance_tools[0]),
    },
    {
        .domain = "personal_health",
        .system_prompt_snippet =
            "[DOMAIN: Personal.Health] Personal health and wellness assistant. Help with symptom tracking"
            ", appointment preparation, medication reminders, health goal setting, nutrition basics, and "
            "health literacy. IMPORTANT: Always recommend consulting a qualified healthcare professional "
            "for medical decisions. This is not medical advice. Compliance: POPIA, Health Professions Act"
            ".",
        .compliance_flags = personal_health_flags,
        .compliance_flag_count = sizeof(personal_health_flags) / sizeof(personal_health_flags[0]),
        .suggested_tools = personal_health_tools,
        .suggested_tool_count = sizeof(personal_health_tools) / sizeof(personal_health_tools[0]),
    },
    {
        .domain = "personal_mental",
        .system_prompt_snippet =
            "[DOMAIN: Personal.Mental] Warm, empathetic mental wellness companion. Offer emotional check-"
            "ins, mindfulness exercises, evidence-based coping strategies (CBT, DBT basics), and psychoed"
            "ucation. Never diagnose. Always validate feelings before offering tools. IMPORTANT: For cris"
            "is situations, always direct to emergency services or SADAG (0800 456 789). Not a substitute"
            " for professional therapy. Compliance: POPIA, Mental Health Care Act.",
        .compliance_flags = personal_mental_flags,
        .compliance_flag_count = sizeof(personal_mental_flags) / sizeof(personal_mental_flags[0]),
        .suggested_tools = personal_mental_tools,
        .suggested_tool_count = sizeof(personal_mental_tools) / sizeof(personal_mental_tools[0]),
    },
    {
        .domain = "pets",
        .system_prompt_snippet =
            "[DOMAIN: Pets] Expert pet care companion. Help with nutrition advice, training techniques (p"
            "ositive reinforcement), health symptom triage (recommend vet for medical decisions), breed-s"
            "pecific care, and emergency first aid basics. Compliance: Animals Protection Act 71/1962, PO"
            "PIA.",
        .compliance_flags = pets_flags,
        .compliance_flag_count = sizeof(pets_flags) / sizeof(pets_flags[0]),
        .suggested_tools = pets_tools,
        .suggested_tool_count = sizeof(pets_tools) / sizeof(pets_tools[0]),
    },
    {
        .domain = "real_estate",
        .system_prompt_snippet =
            "[DOMAIN: RealEstate] Expert real estate assistant. Help with property market analysis, valua"
            "tion frameworks, lease and sale agreement review, conveyancing timelines, sectional title ru"
            "les, and rental management. Ground advice in current market data. Compliance: Alienation of "
            "Land Act, Rental Housing Act, PPRA, FICA, POPIA.",
        .compliance_flags = real_estate_flags,
        .compliance_flag_count = sizeof(real_estate_flags) / sizeof(real_estate_flags[0]),
        .suggested_tools = real_estate_tools,
        .suggested_tool_count = sizeof(real_estate_tools) / sizeof(real_estate_tools[0]),
    },
    {
        .domain = "relationships",
        .system_prompt_snippet =
            "[DOMAIN: Relationships] Empathetic relationship support companion. Help with communication s"
            "trategies, conflict resolution (NVC principles), relationship goal-setting, and self-reflect"
            "ion prompts. Non-judgmental, no-advice-without-consent approach. Not a therapy service. Comp"
            "liance: POPIA.",
        .compliance_flags = relationships_flags,
        .compliance_flag_count = sizeof(relationships_flags) / sizeof(relationships_flags[0]),
        .suggested_tools = relationships_tools,
        .suggested_tool_count = sizeof(relationships_tools) / sizeof(relationships_tools[0]),
    },
    {
        .domain = "retail",
        .system_prompt_snippet =
            "[DOMAIN: Retail] Expert retail operations assistant. Help with stock replenishment, planogra"
            "m optimisation, shrinkage reduction, seasonal promotions, customer loyalty, and sales floor "
            "management. Ground advice in margin and sell-through rates. Compliance: Consumer Protection "
            "Act, POPIA.",
        .compliance_flags = retail_flags,
        .compliance_flag_count = sizeof(retail_flags) / sizeof(retail_flags[0]),
        .suggested_tools = retail_tools,
        .suggested_tool_count = sizeof(retail_tools) / sizeof(retail_tools[0]),
    },
    {
        .domain = "safety",
        .system_prompt_snippet =
            "[DOMAIN: Safety] Personal safety and emergency preparedness assistant. Help with home securi"
            "ty assessments, emergency response plans, first aid guidance (always recommend professional "
            "training), situational awareness tips, and crisis communication. IMPORTANT: For life-threate"
            "ning emergencies, direct immediately to 10111 (SAPS) or 10177 (ambulance). Compliance: POPIA"
            ", OHS Act.",
        .compliance_flags = safety_flags,
        .compliance_flag_count = sizeof(safety_flags) / sizeof(safety_flags[0]),
        .suggested_tools = safety_tools,
        .suggested_tool_count = sizeof(safety_tools) / sizeof(safety_tools[0]),
    },
    {
        .domain = "safety_child",
        .system_prompt_snippet =
            "[DOMAIN: Safety.Child] Child safety and safeguarding assistant for parents and educators. He"
            "lp with online safety education, age-appropriate device rules, recognising grooming signs, r"
            "eporting abuse, and digital literacy. Always prioritise child welfare. IMPORTANT: For immedi"
            "ate child safety concerns, contact SAPS (10111) or Childline (116). Compliance: Children's A"
            "ct 38/2005, POPIA (children's data), FILMS_PUBLICATIONS_ACT, Cybercrimes Act.",
        .compliance_flags = safety_child_flags,
        .compliance_flag_count = sizeof(safety_child_flags) / sizeof(safety_child_flags[0]),
        .suggested_tools = safety_child_tools,
        .suggested_tool_count = sizeof(safety_child_tools) / sizeof(safety_child_tools[0]),
    },
    {
        .domain = "social",
        .system_prompt_snippet =
            "[DOMAIN: Social] Expert social media and community management assistant. Help with platform-"
            "specific content creation (LinkedIn, Instagram, TikTok, X, Facebook), engagement strategy, h"
            "ashtag research, influencer brief writing, community moderation guidelines, and social analy"
            "tics. Apply scroll-stopping creative principles. Compliance: POPIA, ASA Advertising Code, pl"
            "atform community standards.",
        .compliance_flags = social_flags,
        .compliance_flag_count = sizeof(social_flags) / sizeof(social_flags[0]),
        .suggested_tools = social_tools,
        .suggested_tool_count = sizeof(social_tools) / sizeof(social_tools[0]),
    },
    {
        .domain = "sports",
        .system_prompt_snippet =
            "[DOMAIN: Sports] Expert sports management and performance assistant. Help with training prog"
            "ramme design, athlete nutrition guidance, club administration, fixture scheduling, performan"
            "ce data analysis, and sports event management. Apply periodisation and load management princ"
            "iples. Compliance: WADA anti-doping rules, SASCOC, Sport and Recreation SA, POPIA.",
        .compliance_flags = sports_flags,
        .compliance_flag_count = sizeof(sports_flags) / sizeof(sports_flags[0]),
        .suggested_tools = sports_tools,
        .suggested_tool_count = sizeof(sports_tools) / sizeof(sports_tools[0]),
    },
    {
        .domain = "tourism",
        .system_prompt_snippet =
            "[DOMAIN: Tourism] Expert tourism and travel operations assistant. Help with itinerary design"
            ", tour package costing, guide briefing notes, destination marketing, and safety management p"
            "lans. Apply experiential travel principles. Compliance: Tourism Act 3/2014, SABS tour operat"
            "or standards, SATSA, POPIA.",
        .compliance_flags = tourism_flags,
        .compliance_flag_count = sizeof(tourism_flags) / sizeof(tourism_flags[0]),
        .suggested_tools = tourism_tools,
        .suggested_tool_count = sizeof(tourism_tools) / sizeof(tourism_tools[0]),
    },
    {
        .domain = "travel",
        .system_prompt_snippet =
            "[DOMAIN: Travel] Expert travel planning companion. Help with trip itinerary building, visa a"
            "nd entry requirements, budget travel strategies, packing lists, travel insurance guidance, a"
            "nd safety advisories. Personalise to the traveller profile. Compliance: POPIA, Consumer Prot"
            "ection Act (travel packages).",
        .compliance_flags = travel_flags,
        .compliance_flag_count = sizeof(travel_flags) / sizeof(travel_flags[0]),
        .suggested_tools = travel_tools,
        .suggested_tool_count = sizeof(travel_tools) / sizeof(travel_tools[0]),
    },
};

static const size_t g_domain_count = sizeof(g_domains) / sizeof(g_domains[0]);

size_t ca_domain_context_count(void) { return g_domain_count; }

const ca_domain_context_t *ca_domain_context_at(size_t index) {
    return index < g_domain_count ? &g_domains[index] : NULL;
}

const ca_domain_context_t *ca_domain_context_find(const char *domain) {
    if (!domain) return NULL;
    for (size_t i = 0; i < g_domain_count; i++) {
        if (strcmp(g_domains[i].domain, domain) == 0) return &g_domains[i];
    }
    return NULL;
}

const ca_domain_context_t *ca_accessibility_domain_context(void) { return &g_domains[0]; }
const ca_domain_context_t *ca_agriculture_domain_context(void) { return &g_domains[1]; }
const ca_domain_context_t *ca_beauty_domain_context(void) { return &g_domains[2]; }
const ca_domain_context_t *ca_business_domain_context(void) { return &g_domains[3]; }
const ca_domain_context_t *ca_civic_domain_context(void) { return &g_domains[4]; }
const ca_domain_context_t *ca_commerce_domain_context(void) { return &g_domains[5]; }
const ca_domain_context_t *ca_commerce_accounting_domain_context(void) { return &g_domains[6]; }
const ca_domain_context_t *ca_commerce_finance_domain_context(void) { return &g_domains[7]; }
const ca_domain_context_t *ca_commerce_integration_pay_fast_domain_context(void) { return &g_domains[8]; }
const ca_domain_context_t *ca_commerce_integration_xero_domain_context(void) { return &g_domains[9]; }
const ca_domain_context_t *ca_community_domain_context(void) { return &g_domains[10]; }
const ca_domain_context_t *ca_construction_domain_context(void) { return &g_domains[11]; }
const ca_domain_context_t *ca_creative_domain_context(void) { return &g_domains[12]; }
const ca_domain_context_t *ca_education_domain_context(void) { return &g_domains[13]; }
const ca_domain_context_t *ca_elderly_domain_context(void) { return &g_domains[14]; }
const ca_domain_context_t *ca_energy_domain_context(void) { return &g_domains[15]; }
const ca_domain_context_t *ca_faith_domain_context(void) { return &g_domains[16]; }
const ca_domain_context_t *ca_family_domain_context(void) { return &g_domains[17]; }
const ca_domain_context_t *ca_fitness_domain_context(void) { return &g_domains[18]; }
const ca_domain_context_t *ca_food_domain_context(void) { return &g_domains[19]; }
const ca_domain_context_t *ca_gaming_domain_context(void) { return &g_domains[20]; }
const ca_domain_context_t *ca_hr_domain_context(void) { return &g_domains[21]; }
const ca_domain_context_t *ca_healthcare_domain_context(void) { return &g_domains[22]; }
const ca_domain_context_t *ca_home_domain_context(void) { return &g_domains[23]; }
const ca_domain_context_t *ca_hospitality_domain_context(void) { return &g_domains[24]; }
const ca_domain_context_t *ca_kids_domain_context(void) { return &g_domains[25]; }
const ca_domain_context_t *ca_legal_domain_context(void) { return &g_domains[26]; }
const ca_domain_context_t *ca_logistics_domain_context(void) { return &g_domains[27]; }
const ca_domain_context_t *ca_media_domain_context(void) { return &g_domains[28]; }
const ca_domain_context_t *ca_parenting_domain_context(void) { return &g_domains[29]; }
const ca_domain_context_t *ca_personal_domain_context(void) { return &g_domains[30]; }
const ca_domain_context_t *ca_personal_finance_domain_context(void) { return &g_domains[31]; }
const ca_domain_context_t *ca_personal_health_domain_context(void) { return &g_domains[32]; }
const ca_domain_context_t *ca_personal_mental_domain_context(void) { return &g_domains[33]; }
const ca_domain_context_t *ca_pets_domain_context(void) { return &g_domains[34]; }
const ca_domain_context_t *ca_real_estate_domain_context(void) { return &g_domains[35]; }
const ca_domain_context_t *ca_relationships_domain_context(void) { return &g_domains[36]; }
const ca_domain_context_t *ca_retail_domain_context(void) { return &g_domains[37]; }
const ca_domain_context_t *ca_safety_domain_context(void) { return &g_domains[38]; }
const ca_domain_context_t *ca_safety_child_domain_context(void) { return &g_domains[39]; }
const ca_domain_context_t *ca_social_domain_context(void) { return &g_domains[40]; }
const ca_domain_context_t *ca_sports_domain_context(void) { return &g_domains[41]; }
const ca_domain_context_t *ca_tourism_domain_context(void) { return &g_domains[42]; }
const ca_domain_context_t *ca_travel_domain_context(void) { return &g_domains[43]; }


/* ------------------------------------------------------------------------
 * The hand-written half. Everything above this line is generated from the C#
 * by tools/gen_dc.py; everything below is the behaviour, and is not.
 * ------------------------------------------------------------------------ */

char *ca_domain_context_enrich(const ca_domain_context_t *ctx, const char *message) {
    if (!ctx || !ctx->system_prompt_snippet) return NULL;

    /* A NULL or empty turn still gets the snippet. What the model is being
     * told about itself does not depend on whether the person said anything,
     * and returning a bare empty string here would silently drop the domain. */
    const char *msg = message ? message : "";

    const size_t snip_len = strlen(ctx->system_prompt_snippet);
    const size_t msg_len = strlen(msg);

    /* snippet + "\n\n" + message + NUL */
    char *out = (char *)malloc(snip_len + 2 + msg_len + 1);
    if (!out) return NULL;

    memcpy(out, ctx->system_prompt_snippet, snip_len);
    out[snip_len] = '\n';
    out[snip_len + 1] = '\n';
    memcpy(out + snip_len + 2, msg, msg_len);
    out[snip_len + 2 + msg_len] = '\0';
    return out;
}

int ca_domain_context_has_flag(const ca_domain_context_t *ctx, const char *flag) {
    if (!ctx || !flag) return 0;
    for (size_t i = 0; i < ctx->compliance_flag_count; i++) {
        if (strcmp(ctx->compliance_flags[i], flag) == 0) return 1;
    }
    return 0;
}

int ca_domain_context_suggests_tool(const ca_domain_context_t *ctx, const char *tool) {
    if (!ctx || !tool) return 0;
    for (size_t i = 0; i < ctx->suggested_tool_count; i++) {
        if (strcmp(ctx->suggested_tools[i], tool) == 0) return 1;
    }
    return 0;
}
