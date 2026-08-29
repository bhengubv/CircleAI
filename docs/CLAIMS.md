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
| The forgetting curve is calibrated for a memory, not for one human exposure | unproven | 14 days initial stability was reasoned, not fitted to anything |
| Decay behaves well over a real year of use | unproven | no fixture spans more than a simulated year |

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

Measured on a Huawei P30 Lite (`MAR-LX1M`, `UTKDU19919000815`), not an emulator.

| Claim | Status | Evidence |
|---|---|---|
| FTS5 is available on Android, not the LIKE floor | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| Recall completes in 120-190 ms | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| Recording an atom costs about 200 ms | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| Replaying the whole log costs 245 ms | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| Learning from a conversation costs 571 ms, no model | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| The memory survives the app being killed | measured | P30 Lite MAR-LX1M, 2026-08-28 |
| isiZulu is readable in the log on the device | measured | P30 Lite MAR-LX1M, 2026-08-28 |

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
| Recall stays fast at thousands of atoms | unproven | no fixture is larger than a few dozen |
| The device numbers are a gate | unproven | MemoryProbe reports; nothing fails if they regress |
| Three machines actually share one folder | unproven | only one machine has it installed |
| A model reads conversations better than the cues do | unproven | IAtomExtractor has one implementation |
