#ifndef CIRCLE_AI_SIMULATION_H
#define CIRCLE_AI_SIMULATION_H

/*
 * simulation.h — CircleAI.Simulation (C11 port of GraphNode.cs / GraphEdge.cs /
 * KnowledgeGraph.cs / SimulationScenario.cs / SimulationResult.cs /
 * IGraphBuilder.cs / ISimulationEngine.cs / EpisodicGraphExtractor.cs /
 * NetworkHealthSimulator.cs (+ LocalSimulationEngine) / MiroFishAdapter.cs /
 * ThreatPropagationScenario.cs).
 *
 *   Graph   : GraphNode(Id, Label, Kind, Properties{}, ExtractedAt);
 *             GraphEdge(Id, SourceId, TargetId, Relation, Weight[0..1], CreatedAt);
 *             KnowledgeGraph — AddNode/AddEdge (Id keyed, last-write wins),
 *             EdgesFor(nodeId), ReachableFrom(startId) (BFS), Merge(other).
 *   Scenario: ScenarioKind { ConfigurationShift, DataPipelineChange,
 *             SoftwareDeployment, SecurityPatch, ThreatPropagation };
 *             SimulationScenario(Id, Kind, Description, Parameters{}, StepCount,
 *             CreatedAt).
 *   Result  : SimulationOutcome { Healthy, Degraded, Critical, Unknown };
 *             SimulationResult(ScenarioId, Outcome, HealthScore, Findings[],
 *             Recommendations[], StepsRun, CompletedAt).
 *   Extract : EpisodicGraphExtractor.Build(entries[]) — event/app/topic nodes +
 *             occurred_in / tagged_with / followed_by edges.
 *   Engine  : LocalSimulationEngine / MiroFishAdapter.Run(scenario, graph) —
 *             deterministic diffusion (decay 0.01/step per edge, high-impact
 *             node collection at weight >= 0.7). NetworkHealthSimulator.Forecast
 *             (history[], scenario) = extract then run.
 *   Threat  : ThreatPropagationScenario.FromAnomalySignal(signal, stepOverride?)
 *             — ThreatPropagation scenario with a per-vector step count.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Ids as
 * UUID strings; timestamps as int64 Unix ms UTC. Linear arrays, no pthreads.
 * Pure C11 + libc. Consumes memory_brain.h (episodic) + security.h (anomaly).
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "memory_brain.h" /* ca_episodic_entry_t */
#include "security.h"     /* ca_anomaly_signal_t, ca_threat_vector_t */

#ifdef __cplusplus
extern "C" {
#endif

/* key/value pair (node Properties / scenario Parameters). */
typedef struct { char *key; char *value; } ca_sim_kv_t;

/* GraphNode(Id, Label, Kind, Properties{}, ExtractedAt). */
typedef struct {
    char        *id;         /* owned, non-null UUID string */
    char        *label;      /* owned, non-null */
    char        *kind;       /* owned, non-null */
    ca_sim_kv_t *properties; /* owned; NULL when property_count == 0 */
    size_t       property_count;
    int64_t      extracted_at_ms;
} ca_graph_node_t;

void ca_graph_node_free(ca_graph_node_t *n);
void ca_graph_node_free_array(ca_graph_node_t *arr, size_t count);
/* Create(label, kind, props?) at now_ms -> fresh node into *out, true; false on
 * OOM. Stamps a new UUID id. */
bool ca_graph_node_create(const char *label, const char *kind,
                          const ca_sim_kv_t *props, size_t prop_count,
                          int64_t now_ms, ca_graph_node_t *out);

/* GraphEdge(Id, SourceId, TargetId, Relation, Weight, CreatedAt). */
typedef struct {
    char   *id;         /* owned, non-null UUID string */
    char   *source_id;  /* owned, non-null */
    char   *target_id;  /* owned, non-null */
    char   *relation;   /* owned, non-null */
    float   weight;     /* clamped to [0, 1] */
    int64_t created_at_ms;
} ca_graph_edge_t;

void ca_graph_edge_free(ca_graph_edge_t *e);
void ca_graph_edge_free_array(ca_graph_edge_t *arr, size_t count);
/* Create(sourceId, targetId, relation, weight=1) at now_ms -> fresh edge, weight
 * clamped to [0,1]. false on OOM. Stamps a new UUID id. */
bool ca_graph_edge_create(const char *source_id, const char *target_id,
                          const char *relation, float weight, int64_t now_ms,
                          ca_graph_edge_t *out);

/* ── KnowledgeGraph ─────────────────────────────────────────────────────── */

typedef struct ca_knowledge_graph_sim ca_knowledge_graph_sim_t;

ca_knowledge_graph_sim_t *ca_knowledge_graph_sim_create(void); /* NULL on OOM */
void ca_knowledge_graph_sim_destroy(ca_knowledge_graph_sim_t *g);

/* AddNode/AddEdge — Id keyed (last-write wins). 0 / -1 on bad args / OOM. */
int ca_knowledge_graph_sim_add_node(ca_knowledge_graph_sim_t *g,
                                    const ca_graph_node_t *node);
int ca_knowledge_graph_sim_add_edge(ca_knowledge_graph_sim_t *g,
                                    const ca_graph_edge_t *edge);
size_t ca_knowledge_graph_sim_node_count(const ca_knowledge_graph_sim_t *g);
size_t ca_knowledge_graph_sim_edge_count(const ca_knowledge_graph_sim_t *g);

/* EdgesFor(nodeId) — edges where nodeId is source or target. NULL + 0 empty;
 * NULL + SIZE_MAX on error (nodeId required). */
ca_graph_edge_t *ca_knowledge_graph_sim_edges_for(const ca_knowledge_graph_sim_t *g,
                                                  const char *node_id,
                                                  size_t *out_count);
/* ReachableFrom(startId) — BFS incl. the start node. NULL + 0 empty; NULL +
 * SIZE_MAX on error. */
ca_graph_node_t *ca_knowledge_graph_sim_reachable_from(const ca_knowledge_graph_sim_t *g,
                                                       const char *start_id,
                                                       size_t *out_count);
/* Merge(other) into g (last-write wins). 0 / -1. */
int ca_knowledge_graph_sim_merge(ca_knowledge_graph_sim_t *g,
                                 const ca_knowledge_graph_sim_t *other);

/* ── Scenario / Result ──────────────────────────────────────────────────── */

typedef enum {
    CA_SCENARIO_CONFIGURATION_SHIFT   = 0,
    CA_SCENARIO_DATA_PIPELINE_CHANGE  = 1,
    CA_SCENARIO_SOFTWARE_DEPLOYMENT   = 2,
    CA_SCENARIO_SECURITY_PATCH        = 3,
    CA_SCENARIO_THREAT_PROPAGATION    = 4
} ca_scenario_kind_t;

typedef struct {
    char              *id;          /* owned, non-null UUID string */
    ca_scenario_kind_t kind;
    char              *description; /* owned, non-null */
    ca_sim_kv_t       *parameters;  /* owned; NULL when parameter_count == 0 */
    size_t             parameter_count;
    int                step_count;
    int64_t            created_at_ms;
} ca_simulation_scenario_t;

void ca_simulation_scenario_free(ca_simulation_scenario_t *s);
/* Create(kind, description, params?, steps=10) at now_ms -> fresh scenario. false
 * on OOM. Stamps a new UUID id. */
bool ca_simulation_scenario_create(ca_scenario_kind_t kind, const char *description,
                                   const ca_sim_kv_t *params, size_t param_count,
                                   int steps, int64_t now_ms,
                                   ca_simulation_scenario_t *out);

typedef enum {
    CA_SIM_OUTCOME_HEALTHY  = 0,
    CA_SIM_OUTCOME_DEGRADED = 1,
    CA_SIM_OUTCOME_CRITICAL = 2,
    CA_SIM_OUTCOME_UNKNOWN  = 3
} ca_simulation_outcome_t;

typedef struct {
    char                   *scenario_id;      /* owned, non-null */
    ca_simulation_outcome_t outcome;
    float                   health_score;     /* [0, 1] */
    char                  **findings;         /* owned; NULL when finding_count == 0 */
    size_t                  finding_count;
    char                  **recommendations;  /* owned; NULL when recommendation_count == 0 */
    size_t                  recommendation_count;
    int                     steps_run;
    int64_t                 completed_at_ms;
} ca_simulation_result_t;

void ca_simulation_result_free(ca_simulation_result_t *r);

/* ── EpisodicGraphExtractor.Build ───────────────────────────────────────── */

/* Build a graph from episodic entries (now_ms stamps generated nodes/edges).
 * Returns a fresh graph (caller destroys), or NULL on OOM. */
ca_knowledge_graph_sim_t *ca_episodic_graph_extractor_build(
    const ca_episodic_entry_t *entries, size_t entry_count, int64_t now_ms);

/* ── LocalSimulationEngine / MiroFishAdapter.Run ────────────────────────── */

/* Run(scenario, graph) at completed_ms -> fresh result into *out, true; false on
 * bad args / OOM. (LocalSimulationEngine == MiroFishAdapter fallback.) */
bool ca_simulation_engine_run(const ca_simulation_scenario_t *scenario,
                              const ca_knowledge_graph_sim_t *graph,
                              int64_t completed_ms, ca_simulation_result_t *out);

/* NetworkHealthSimulator.Forecast(history[], scenario) — extract then run. */
bool ca_network_health_forecast(const ca_episodic_entry_t *history, size_t history_count,
                                const ca_simulation_scenario_t *scenario,
                                int64_t now_ms, ca_simulation_result_t *out);

/* ── ThreatPropagationScenario.FromAnomalySignal ────────────────────────── */

/* FromAnomalySignal(signal, stepOverride) — step_override < 0 means derive the
 * step count from the vector. Returns a ThreatPropagation scenario into *out,
 * true; false on bad args / OOM. */
bool ca_threat_propagation_from_anomaly(const ca_anomaly_signal_t *signal,
                                        int step_override, int64_t now_ms,
                                        ca_simulation_scenario_t *out);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SIMULATION_H */
