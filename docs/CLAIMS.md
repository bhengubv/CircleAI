# What this claims, and what backs it

Every claim the memory makes about itself, with the evidence for it. **This file
is under test.** `GenuineProductTests` parses the table below and fails the
build if a row marked `tested` names a test that does not exist, or if a row
marked `measured` does not say on what and when.

That is the point of it. A README can say anything; a row here cannot.

**Status means:**

| | |
|---|---|
| `tested` | An automated test asserts it. Evidence is that test's name, and it must exist. |
| `measured` | Observed on real hardware. Evidence must name the device and the date. |
| `unproven` | Written and not verified. Evidence must say what is missing. **Do not rely on it.** |

---

## Honesty — the memory does not invent

| Claim | Status | Evidence |
|---|---|---|
| An extracted atom is a verbatim quote of what was said, never a paraphrase | tested | Nothing_it_kept_was_words_nobody_said |
| Nothing comes back that was never recorded | tested | Recall_can_only_return_what_was_put_in |
| A correction never rewrites what it replaced | tested | Correcting_leaves_the_earlier_version_byte_exact |
| Every atom can be traced to what was said | tested | Every_atom_says_where_it_came_from |
| Text is unchanged through log, index and recall | tested | Any_rule_comes_back_in_the_words_it_was_written_in |
| Extraction reads the person, never the assistant | tested | It_does_not_listen_to_the_assistant |
| Extraction never supersedes on its own | tested | It_never_supersedes_on_its_own |
| An uncertain reading is offered, not kept | tested | It_offers_what_it_is_not_sure_of_instead_of_keeping_it |

## Recall

| Claim | Status | Evidence |
|---|---|---|
| A rule comes back for the situation it was filed under | tested | Any_rule_comes_back_for_the_situation_it_was_filed_under |
| A narrower situation still finds a broader rule | tested | A_narrower_situation_still_finds_the_broader_atom |
| A rule is findable by a fragment of its own words | tested | Any_rule_is_findable_by_the_words_it_is_made_of |
| Languages with no spaces are findable too | tested | Any_rule_is_findable_by_the_words_it_is_made_of |
| A standing rule outranks a leaning | tested | A_standing_rule_outranks_a_leaning_on_the_same_moment |
| What had to be repeated arrives first | tested | What_had_to_be_repeated_arrives_first |
| A road already tried and closed ranks near the top | tested | It_would_have_answered_before_the_deploy_that_cost_a_day |
| Recall stays inside its budget | tested | Recall_stays_inside_its_budget_however_many_rules_there_are |
| A superseded atom stops answering and stays readable | tested | A_rule_that_changed_stops_answering_and_stays_readable |
| Nothing known returns nothing rather than noise | tested | Nothing_known_returns_nothing_rather_than_noise |

## Anybody's rules, not just ours

| Claim | Status | Evidence |
|---|---|---|
| The properties hold for more than one person's rules | tested | There_is_more_than_one_persons_rules_under_test |
| They hold for rules that are not in English | tested | There_is_more_than_one_persons_rules_under_test |
| A person's markdown is read the same way whoever wrote it | tested | A_persons_markdown_is_read_the_same_way_whoever_wrote_it |
| Any rule is readable in the log that carries it | tested | Any_rule_is_readable_in_the_log_that_carries_it |

## Forgetting

A store that keeps everything at full volume forever is a filing cabinet. Two
strengths, following Bjork: **stability** (how deeply learned, only ever grows)
and **retrievability** (how reachable now, decays with time, restored by use).

| Claim | Status | Evidence |
|---|---|---|
| What goes unused fades out of what recall offers | tested | A_decision_about_one_afternoon_is_allowed_to_fade |
| Fading is not deleting - it is still in the log and still there by id | tested | What_faded_is_still_there_when_you_go_looking |
| Something faded comes back when it is needed again | tested | Something_faded_comes_back_when_it_is_needed_again |
| Reaching for something makes it easier to reach next time | tested | Reaching_for_something_makes_it_easier_to_reach_next_time |
| Rescuing something at the edge is worth more than touching it twice | tested | Rescuing_something_at_the_edge_is_worth_more_than_touching_it_twice |
| Asking the same thing twice in a minute barely counts | tested | Asking_the_same_thing_twice_in_a_minute_barely_counts |
| How deeply a thing is learned never goes down | tested | How_deeply_a_thing_is_learned_never_goes_down |
| Being corrected makes a thing stick | tested | Being_corrected_makes_a_thing_stick |
| A standing rule does not go quiet because a year passed | tested | A_standing_rule_does_not_go_quiet_because_a_year_passed |
| Recall strengthens only what it actually handed back | tested | Recall_strengthens_only_what_it_actually_handed_back |
| What has been used arrives before what has not | tested | What_has_been_used_arrives_before_what_has_not |
| Wear is local and never travels between machines | tested | Wear_does_not_travel_between_machines |
| Wear survives the index being thrown away | tested | Wear_survives_the_index_being_thrown_away |
| A broken wear file costs familiarity and nothing else | tested | A_wear_file_somebody_broke_costs_familiarity_and_nothing_else |
| A store with no sense of use still works unchanged | tested | Without_wear_nothing_fades_and_nothing_breaks |
| The initial stability is solved for, not chosen | tested | The_number_is_solved_for_not_chosen |
| No other value considered satisfies both requirements | tested | Every_other_value_that_was_considered_fails_one_of_them |
| A thing needed twice a year is there the second time | tested | The_thing_that_comes_round_twice_a_year_is_there_when_it_does |
| The same attention spread out is worth more than crammed | tested | The_same_attention_spread_out_is_worth_more_than_crammed |
| Coming back after months makes a rare thing durable | tested | Coming_back_after_months_is_what_makes_a_rare_thing_durable |
| After a simulated year the right things are still in reach | tested | After_a_year_the_right_things_are_still_within_reach |
| The working set stops growing even though the memory does not | tested | The_working_set_stops_growing_even_though_the_memory_does_not |
| Nothing that faded over the year was lost | tested | Nothing_that_faded_was_lost |
| The two requirements that pin the number are the right requirements | unproven | 230 days to survive and 355 to fade are stated and arguable, not observed |
| Decay behaves this way for a real person over a real year | unproven | the year is simulated; nobody has used it for one |

## A service every module consumes

Memory is not a feature one app has. There is one memory per device and
everything that wants continuity takes it - including the modules that must not
retain anything, because "never keep this" is itself something that has to be
remembered.

| Claim | Status | Evidence |
|---|---|---|
| An app holds one memory that survives being killed | tested | What_was_remembered_survives_the_app_being_killed |
| Wear survives being killed, not just being closed | tested | The_wear_survives_the_app_being_killed |
| Nothing is held back waiting for a lifecycle callback | tested | Nothing_is_held_back_waiting_for_a_callback |
| Closing it lets go of the database file | tested | Closing_it_lets_go_of_the_file |
| Several threads can ask and remember at once | tested | An_app_can_ask_and_remember_from_several_threads_at_once |
| A device gets one memory however many times it is registered | tested | Registering_it_twice_does_not_give_a_device_two_memories |
| An interpreter remembers that it must not remember | tested | An_interpreter_remembers_that_it_must_not_remember |
| A module that may keep only rules keeps none of what passed through it | tested | An_interpreter_does_not_keep_what_passed_through_it |
| A gate never remembers that something was allowed | tested | A_gate_never_remembers_that_something_was_allowed |
| Retention never restricts reading | tested | Reading_is_never_restricted_by_retention |
| What a module may keep is declared in code, not read from the memory | tested | What_a_module_may_keep_is_declared_in_code_not_read_from_the_memory |
| What a module recorded can be told apart from what another did | tested | Two_modules_recording_the_same_words_stay_apart |
| Learning does not get slower as the memory fills | tested | Learning_does_not_get_slower_as_the_memory_fills |
| An update that changes the schema does not break the memory | tested | An_update_that_changes_the_schema_does_not_break_the_memory |
| Every CircleAI module consumes it | unproven | two heads consume it; roughly 150 modules do not |
| It is ready to be ported to another language | unproven | the curve has no generated fixtures, the cue table is English and in code |

## Three machines

| Claim | Status | Evidence |
|---|---|---|
| Each machine writes only its own log, so git never merges | tested | Each_machine_writes_only_its_own_file |
| A correction on one machine supersedes a decision from another | tested | A_correction_on_one_machine_supersedes_a_decision_from_another |
| Rules written on three machines read as one memory | tested | Rules_written_on_three_machines_read_as_one_memory |
| Replay is ordered by clock, not by which file was read first | tested | Replay_orders_by_time_not_by_which_file_was_read_first |
| Rebuilding twice changes nothing | tested | Rebuilding_twice_changes_nothing |
| Losing the index never loses a rule | tested | Losing_the_index_never_loses_a_rule |
| Reading without an index agrees with reading through one | tested | Reading_without_an_index_agrees_with_reading_through_one |
| A hand-mangled line does not cost the rest | tested | A_line_somebody_mangled_by_hand_does_not_cost_the_rest |
| Two phones never write to one log | tested | Two_phones_do_not_end_up_writing_to_one_log |

## Capture

| Claim | Status | Evidence |
|---|---|---|
| It fills itself with no model loaded | tested | It_keeps_what_it_is_sure_of |
| Learning the same conversation twice keeps one of each | tested | Learning_the_same_conversation_twice_keeps_one_of_each |
| The hook takes the words out of an editor's payload | tested | The_hook_takes_the_words_out_of_an_editors_payload |
| The hook treats an envelope with no message as nothing | tested | An_envelope_with_no_message_is_not_something_somebody_said |
| The hook never blocks or erases a prompt | tested | The_hook_never_costs_somebody_their_prompt |

## On the phone

Measured on a Huawei P30 Lite (`MAR-LX1M`, `UTKDU19919000815`) across 2026-08-27 to 30, not an emulator.

| Claim | Status | Evidence |
|---|---|---|
| FTS5 is available on Android, not the LIKE floor | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| Recall completes in 87-190 ms | measured | P30 Lite MAR-LX1M, 2026-08-29 |
| The app's own memory opens in 60-136 ms | measured | P30 Lite MAR-LX1M, 2026-08-29 |
| Reading what was said costs 18 ms | measured | P30 Lite MAR-LX1M, 2026-08-29 |
| The memory carries over a force-stop | measured | P30 Lite MAR-LX1M, 2026-08-29 |
| At 247 real atoms: open 85 ms, recall 39-88 ms, learn 3-6 ms | measured | P30 Lite MAR-LX1M, 2026-08-29 |
| An app update that changes the schema rebuilds in 3.1 s and loses nothing | measured | P30 Lite MAR-LX1M, 2026-08-29 |
| WAL takes a write from 346 ms to 1.4 ms | measured | desktop SSD, 2026-08-29 |
| Replaying the whole log costs 245 ms | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| Learning from a conversation costs 571 ms, no model | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| The memory survives the app being killed | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| isiZulu is readable in the log on the device | measured | P30 Lite MAR-LX1M, 2026-08-28 |

## Setup and the loading screen

The honesty screen: on every launch it says, in plain words, what this phone
can and cannot do. Measured on the P30 Lite.

| Claim | Status | Evidence |
|---|---|---|
| The census shows what is present and what is missing, every row named | measured | P30 Lite MAR-LX1M, 2026-08-30 |
| On a clean phone it shows 0 OF 5 with real sizes and downloads on its own | measured | P30 Lite MAR-LX1M, 2026-08-30 (full uninstall + fresh install, ~800 MB) |
| A partly-set-up phone shows "Finish setting it up", the present rows green | measured | P30 Lite MAR-LX1M, 2026-08-30 |
| Rows turn green as each model lands, not all at once at the end | measured | P30 Lite MAR-LX1M, 2026-08-30 |
| The census is offline: an embedded catalogue and on-disk size checks | measured | P30 Lite MAR-LX1M, 2026-08-30 (milliseconds, was 4 s while it hashed 470 MB) |
| This phone's real RAM is measured, not the managed heap limit | measured | P30 Lite MAR-LX1M, 2026-08-30 (1.3 GB free of 3.6 GB, source PlatformMeasured) |
| The language count is what is installed, not what is catalogued | measured | P30 Lite MAR-LX1M, 2026-08-30 (10 SA languages + English, not the marketing 78) |

## The C# engine — Bonsai 2 in CircleAI's own code

The 2B (qwen35, PTQ1_0) run by `Inference/Managed`, with no native code, judged
against the pinned llama.cpp fork the repo already ships.

| Claim | Status | Evidence |
|---|---|---|
| The 2B's file reads exactly as the pinned fork reads it | tested | The_2B_reads_as_the_pinned_fork_read_it |
| The 2B predicts the fork's tokens, its logits within the fork's own spread | tested | The_2B_predicts_the_forks_tokens_and_logits |
| A conversation gets the fork's prompt and the fork's greedy reply | tested | Each_conversation_gets_the_forks_prompt_and_the_forks_greedy_reply |
| Thinking never leaks into the answer | tested | With_thinking_on_the_reasoning_and_the_answer_come_apart |
| AIService builds the C# engine when the row or the host names it | tested | AIServices_build_site_makes_the_CSharp_engine_when_the_host_names_it |
| A question through AIService comes back with the persona | tested | A_question_through_AIService_is_answered_by_the_CSharp_engine_with_the_persona |
| A tool call comes out in the template's XML form | tested | A_tool_call_comes_out_in_the_templates_form_and_ends_the_turn |
| TurboQuant's fast Hadamard rotation reconstructs as well as the dense rotation | tested | Version_2_reconstructs_as_closely_as_version_1 |
| TurboQuant's second stage leaves inner products unbiased where the first shrinks them | tested | At_equal_bits_Prod_leaves_inner_products_unbiased_where_Mse_shrinks_them |
| Attention on the working memory's codes follows exact attention | tested | Attention_on_the_codes_follows_exact_attention |
| At 4 bits the working memory keeps the fork's tokens, and the MSE form beats the second stage at every width | tested | Each_working_memory_mode_measured_against_the_forks_tokens |
| A compressed working memory survives a session | tested | A_compressed_state_survives_a_session |
| A conversation that opens with the same system turn starts primed, and answers as a cold start does | tested | A_conversation_with_the_same_system_turn_starts_primed_and_answers_as_a_cold_start_does |
| A primed start shortens the first answer on the phone | unproven | not yet run on the P30 |
| The 2B answers on the phone in C#, the desktop's words exactly: 81 prompt tokens in 78 s, 0.94 tokens a second | measured | P30 Lite MAR-LX1M, 2026-10-31 (through the soak broadcast, not the app's own path) |
| It answers through the inference process's own build site on the phone | unproven | built 2026-11-01 and not installed: the phone was not connected |
| It is as fast as the native fork on the same phone | unproven | the fork has never been timed on the P30 |
| The vision tower makes the fork's picture tokens from the same picture | tested | The_tower_makes_the_forks_tokens_from_the_same_picture |
| The 2B reads the picture tokens where the fork places them | tested | The_2B_reads_the_forks_picture_tokens_where_the_fork_places_them |
| A picture is described in the fork's words, and the next turn reuses it | tested | The_picture_is_described_as_the_fork_describes_it_and_a_next_turn_reuses_it |
| The one brain factory builds a 2B that finds its tower and sees | tested | The_one_brain_factory_builds_a_2B_that_finds_its_tower_and_sees |
| A model with no tower refuses a picture instead of answering blind | tested | A_model_with_no_tower_refuses_a_picture_instead_of_answering_blind |
| A photo is described in 34 s: the picture 8.3 s, the 261-token prompt 22 s, 33 tokens in 3.4 s | measured | win-x64 dev PC, 7 threads, 2026-11-02 |
| The 2B sees on the phone | unproven | never run on the P30; its tower runs the scalar path there |

## Choosing the brain

Step 1 of the Bonsai 2 plan: read the device, choose the best brain quick enough to talk with, build
it through one factory, and say why.

| Claim | Status | Evidence |
|---|---|---|
| A brain this device measured too slow to talk with is passed over | tested | The_selector_passes_over_a_brain_too_slow_to_talk_with_on_this_device |
| A brain never run here is estimated from a measured one on the same engine, by its weight | tested | An_unmeasured_brain_is_estimated_from_a_measured_one_on_the_same_engine_by_its_weight |
| Another engine's speed says nothing, and an unknown speed is never too slow | tested | Another_engine_says_nothing_about_this_one_and_unknown_is_never_too_slow |
| The pick is the best quick brain, and the fastest when none is quick | tested | The_pick_is_the_best_quick_brain_and_the_fastest_when_none_is_quick |
| The plan names the brain and the one it passed over, and why | tested | The_plan_names_the_brain_and_the_one_it_passed_over_for_being_too_slow |
| A measured speed survives a relaunch | tested | A_speed_measured_before_a_relaunch_still_rules_the_choice_after_it |
| A brain on the C# engine gets the fast cores less one, read from the kernel | tested | A_brain_gets_the_fast_cores_less_one_and_never_none |
| Every site that builds a brain asks one factory | tested | Every_site_that_builds_a_brain_asks_the_one_factory |
| A file with no catalogue row opens on the engine its contents need | tested | A_file_with_no_row_is_opened_by_the_engine_its_contents_need |
| A Bonsai row on the C# engine counts as on the phone and resolves to its GGUF | tested | A_Bonsai_row_on_the_CSharp_engine_is_present_and_resolves_to_its_gguf |
| The P30 chooses by itself and says why: Qwen3.5-0.8B on MNN, not the Bonsai 4B, estimated at 0.8 tokens a second from the 2B's measured 1.66 on the C# engine | measured | P30 Lite MAR-LX1M, 2026-11-02 (the supervisor's plan line, before and after its inference process was ended) |
| The P30 keeps those speeds across a reinstall and chooses the same way without measuring again | measured | P30 Lite MAR-LX1M, 2026-11-02 (2 records read back at start; the same plan line at start and after the inference process was ended) |

## Tools, effort and translation

Step 2's abilities on the fleet: a tool call held to its form, thought sized to the question, and
translation on the brain the plan picked.

| Claim | Status | Evidence |
|---|---|---|
| The offered tools reach the model through its own template, as the fork renders them | tested | The_offered_tools_reach_the_model_through_its_own_template_as_the_fork_renders_them |
| The 2B's tool call is held to the offered signatures and reads back typed | tested | A_tool_call_round_trips_through_the_generator |
| The agentic loop runs the tool the 2B calls and answers from its result | tested | The_agentic_loop_runs_the_tool_the_2B_calls_and_answers_from_its_result |
| Only the offered tools can be named | tested | Only_the_offered_tools_can_be_named |
| A quick question is answered at once, and a hard one thinks within a budget in the device's own time | tested | A_phone_answers_quick_asks_at_once_and_thinks_medium_about_hard_ones_within_its_speed |
| The thinking budget closes the thought, so the answer still comes | tested | A_call_thinks_when_it_asks_and_its_budget_closes_the_thought_so_the_answer_still_comes |
| How hard to think is learned per device, and a relaunch remembers it | tested | A_later_start_remembers_what_this_device_learned |
| Translation runs on the service's brain as a plain ask through the queue | tested | The_engine_runs_on_the_services_brain_as_a_plain_ask_through_the_queue |
| Spoken translation: the ears transcribe, the brain translates, the voice speaks | tested | The_ears_transcribe_the_brain_translates_and_the_voice_speaks_in_that_order |
| A spoken translation crosses the link as version 2, and transcribing and speaking stay version 1 | tested | A_spoken_translation_crosses_as_version_two_and_comes_back_with_what_was_heard |
| Tools, effort and translation through the brain work on the phone | unproven | never run on the P30 |

## Balance and readiness

What the brain does with the phone's heat and battery, and what it says while it gets ready.

| Claim | Status | Evidence |
|---|---|---|
| A start says when it is warming up, not only that it is loading | tested | A_start_says_it_is_loading_and_then_that_it_is_warming |
| The warming step reaches the brain's status and becomes "Nearly ready to answer." | tested | The_in_process_brain_carries_the_step_into_its_status_and_readiness_into_words |
| A nearly flat battery steps the power budget down without the caller saying so | tested | A_nearly_flat_battery_steps_the_budget_down_without_the_caller_saying_so |
| A hot device steps a high power budget down | tested | A_hot_device_steps_a_high_budget_down |
| MNN's own 4-bit working memory costs the 0.8B its answers, so an MNN brain loads with it off | measured | desktop win-x64 MNN bridge, 2026-11-01 (0 of 6 answers alike at 4 bits; off, 6 of 6 twice) |
| The phone reads its heat and battery while it answers | unproven | built 2026-11-01; not yet run on the P30 |

## Who said what

Telling the voices in a transcribed recording apart (OPEN-GAPS A3).

| Claim | Status | Evidence |
|---|---|---|
| A transcribed file says who spoke each line when the speaker model is on the phone, and invents nobody when it is not | tested | A_transcribed_file_says_who_spoke_each_line_when_there_is_a_speaker_model |
| Setup fetches the speaker model by itself on a build that can transcribe | tested | Setup_fetches_it_on_a_build_that_can_transcribe_and_not_on_one_that_cannot |
| The speaker model is fed its own recipe's features, time first with the utterance mean taken off | tested | The_features_are_time_first_with_the_utterance_mean_taken_off_every_bin |
| How loud a recording was does not change the voice it is read as | tested | How_loud_the_recording_was_does_not_change_the_voice |
| The speaker model tells real voices apart on a phone | unproven | built 2026-11-01; the model has never run here or on the P30 |

## An Italian voice

Kokoro, for the language of its that no other voice in the catalogue speaks (OPEN-GAPS A8).

| Claim | Status | Evidence |
|---|---|---|
| An Italian phone is given an Italian voice by the selector and by setup | tested | An_italian_phone_is_given_it_by_the_selector_and_by_setup |
| Kokoro is handed the symbols its own G2P trained it on, not espeak's raw spelling | tested | An_untied_affricate_becomes_the_one_symbol_kokoro_was_trained_on |
| The Italian voice speaks on a phone, and how fast | unproven | built 2026-11-01; Kokoro has never run here or on the P30 |

## Search by meaning

Kept recordings, found by their words and - with an embedding model - their meaning (OPEN-GAPS A7, A12).

| Claim | Status | Evidence |
|---|---|---|
| A line with no word in common with the query is found by its meaning | tested | A_line_with_no_word_in_common_is_found_by_meaning |
| A search without the model, or with one that fails, still answers by words | tested | A_meaning_leg_that_fails_leaves_the_words |
| Lines are embedded once, kept on disk, and again only when they change | tested | Lines_are_embedded_once_kept_on_disk_and_again_only_when_they_change |
| The vector store finds the nearest on its codes without decoding | tested | The_store_finds_the_nearest_on_its_codes_without_decoding |
| The embedding model runs on a phone, and how long a first search waits | unproven | built 2026-11-01; the model has never run here or on the P30 |

## Domain packs

What a capability tile's chat knows about its subject (OPEN-GAPS A9).

| Claim | Status | Evidence |
|---|---|---|
| Every domain pack in the library is reachable by name, with its own framing | tested | Every_pack_in_the_library_is_in_the_table_with_its_own_framing |
| A tile's chat frames each question the way its pack's adapter does, and an ordinary chat is untouched | tested | A_question_is_framed_the_way_the_packs_own_adapter_frames_it |
| The framing makes a small model's answers better | unproven | built 2026-11-01; not yet compared on a phone |

## Choosing a transport

How a payload picks its way when there is more than one (OPEN-GAPS A10).

| Claim | Status | Evidence |
|---|---|---|
| Transports are tried in the order the contract states, narrowed by what is up and by connectivity | tested | Connectivity_narrows_it |
| A payload falls through a transport that fails to the next one that is up | tested | A_payload_goes_the_first_way_that_is_up_and_falls_through_a_failure |
| The device sends anything over the mesh | unproven | no transport is registered on the device (2026-11-01) |

## Other databases

| Claim | Status | Evidence |
|---|---|---|
| The shared implementation runs against a real engine | tested | Every_field_survives_the_round_trip |
| Each dialect emits SQL of the right shape | tested | Every_dialect_creates_every_column |
| No dialect leaves a search unparameterised | tested | Every_dialect_binds_its_search_terms |
| It works on PostgreSQL | unproven | never sent to a live server |
| It works on SQL Server | unproven | never sent to a live server |
| It works on MySQL | unproven | never sent to a live server |
| It works on Oracle | unproven | never sent to a live server |

## Not built

Named here so nobody has to find out by looking.

| Claim | Status | Evidence |
|---|---|---|
| Recall stays fast at thousands of atoms | unproven | measured at 247 real atoms, not at thousands |
| The device numbers are a gate | unproven | MemoryProbe reports; nothing fails if they regress |
| Three machines actually share one folder | unproven | only one machine has it installed |
| A model reads conversations better than the cues do | unproven | IAtomExtractor has one implementation |
