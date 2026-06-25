# bespoke-adapter-methods.ps1
#
# (3.3.0) Replace the generic 4-method block injected by bulk-up-adapters.ps1
# with bespoke per-vertical methods that fit each domain's real use cases.

$root = Join-Path $PSScriptRoot "..\src"
$adapters = Get-ChildItem -Path $root -Filter "*CompanionAdapter.cs" -Recurse -File

# Per-vertical methods: vertical -> array of {Name, Signature, Prompt}
$Bespoke = @{
    "Accessibility" = @(
        @{ Name="AuditWcagAsync"; Sig="string content, string targetLevel=`"AA`""; Prompt="Audit this content/UI for WCAG 2.2 {targetLevel} compliance: {content}. List violations by criterion id, severity, and a concrete fix."},
        @{ Name="DescribeImageForScreenReaderAsync"; Sig="string imageContext"; Prompt="Write a screen-reader alt-text for the image. Context: {imageContext}. Aim for 1-2 sentences, no 'image of', present tense."},
        @{ Name="SimplifyLanguageAsync"; Sig="string text, string readingAge=`"plain English`""; Prompt="Rewrite this for {readingAge}: {text}. Keep the meaning, drop jargon, use short sentences."},
        @{ Name="SuggestKeyboardShortcutAsync"; Sig="string action, string platform"; Prompt="Suggest an accessible keyboard shortcut for '{action}' on {platform}. Avoid chords that conflict with screen-reader defaults."}
    )
    "Agriculture" = @(
        @{ Name="DiagnoseCropIssueAsync"; Sig="string crop, string symptoms, string region"; Prompt="Diagnose this {crop} issue in {region}: {symptoms}. Cover likely pests/disease/deficiency, confidence, and an integrated-pest-management plan."},
        @{ Name="OptimisePlantingScheduleAsync"; Sig="string crop, string climate, double areaHa"; Prompt="Plan planting for {areaHa}ha of {crop} in {climate}. Include sowing dates, density, irrigation, fertiliser, and harvest window."},
        @{ Name="EstimateYieldAsync"; Sig="string crop, double areaHa, string conditions"; Prompt="Estimate yield (t/ha and total tons) for {areaHa}ha of {crop} under: {conditions}. Show baseline, best, worst case."},
        @{ Name="DraftSustainabilityReportAsync"; Sig="string operationSummary"; Prompt="Draft a sustainability report for: {operationSummary}. Cover soil health, water use, biodiversity, GHG, and SDG alignment."}
    )
    "Ambient" = @(
        @{ Name="SuggestEnvironmentAdjustmentAsync"; Sig="string sensorReadings, string occupantPreference"; Prompt="Given readings: {sensorReadings} and preference: {occupantPreference}. Suggest HVAC / lighting / acoustic adjustments with justification."},
        @{ Name="ExplainAnomalyAsync"; Sig="string sensor, double currentValue, double expectedValue"; Prompt="Explain why {sensor} reads {currentValue} when expected {expectedValue}. List 3 plausible causes + 1 quick check each."},
        @{ Name="DraftOccupancyPolicyAsync"; Sig="string spaceType, int capacity"; Prompt="Draft occupancy + air-quality policy for a {spaceType} of capacity {capacity}. Cite ASHRAE/SANS where relevant."},
        @{ Name="SummariseAmbientTrendAsync"; Sig="string readingsTimeline, int hoursWindow"; Prompt="Summarise this {hoursWindow}h ambient trend: {readingsTimeline}. Highlight excursions and probable causes."}
    )
    "Beauty" = @(
        @{ Name="RecommendRoutineAsync"; Sig="string skinType, string concerns, string budget"; Prompt="Recommend an AM/PM skincare routine for {skinType} skin with {concerns}, budget {budget}. Include ingredient targets and product categories (not brands)."},
        @{ Name="AssessIngredientCompatibilityAsync"; Sig="string ingredientList"; Prompt="Assess this ingredient list for layering safety + irritation risk: {ingredientList}. Flag known clashes (retinol+AHA, vit C+niacinamide, etc.)."},
        @{ Name="DesignTreatmentPlanAsync"; Sig="string clientGoals, int sessionCount"; Prompt="Design a {sessionCount}-session treatment plan to achieve: {clientGoals}. Specify modality, interval, expected progress, and at-home care."},
        @{ Name="DraftBookingConfirmationAsync"; Sig="string clientName, string treatment, string dateTime"; Prompt="Draft a warm booking confirmation message: {clientName}, {treatment}, {dateTime}. Include prep instructions, cancellation policy, location."}
    )
    "Business" = @(
        @{ Name="DraftOkrsForQuarterAsync"; Sig="string unitName, string strategicTheme"; Prompt="Draft 3 objectives × 3 key results for {unitName} aligned to '{strategicTheme}'. KRs must be measurable + time-bound."},
        @{ Name="AnalyseUnitEconomicsAsync"; Sig="string productName, decimal revenue, decimal cogs, decimal marketing"; Prompt="Analyse unit economics for {productName}: revenue {revenue}, COGS {cogs}, marketing {marketing}. Compute gross margin, LTV/CAC sanity, and 3 levers to improve."},
        @{ Name="GenerateBoardUpdateAsync"; Sig="string quarter, string wins, string losses, string asks"; Prompt="Generate a 1-page board update for {quarter}. Wins: {wins}. Losses: {losses}. Asks: {asks}. Use Andy Grove-style brevity."},
        @{ Name="SuggestExperimentAsync"; Sig="string metric, double currentValue, double targetValue"; Prompt="Suggest 3 experiments to move {metric} from {currentValue} to {targetValue}. Score each by impact × confidence × cost."}
    )
    "Civic" = @(
        @{ Name="DraftPetitionAsync"; Sig="string issue, string targetOffice, int signatureGoal"; Prompt="Draft a clear, factual petition on '{issue}' to {targetOffice}, targeting {signatureGoal} signatures. Include problem, ask, evidence, signature ask."},
        @{ Name="LogServiceFailureAsync"; Sig="string serviceName, string location, string failureDescription"; Prompt="Compose a service-failure report for {serviceName} at {location}: {failureDescription}. Format for municipal ticketing systems."},
        @{ Name="ExplainPolicyAsync"; Sig="string policyName, string audience"; Prompt="Explain '{policyName}' to a {audience}. Cover what it does, who's affected, and what to do if it affects you."},
        @{ Name="PrepareCouncilQuestionsAsync"; Sig="string topic, int questionCount"; Prompt="Prepare {questionCount} pointed questions for council on {topic}. Each should be specific, evidence-based, and require a substantive answer."}
    )
    "Commerce" = @(
        @{ Name="WriteProductDescriptionAsync"; Sig="string productName, string features, string targetCustomer"; Prompt="Write a product description for {productName} aimed at {targetCustomer}. Features: {features}. Use the 'feature → benefit' pattern, end with a CTA."},
        @{ Name="AnalyseConversionFunnelAsync"; Sig="string funnelMetrics"; Prompt="Analyse this funnel: {funnelMetrics}. Identify the biggest drop-off, the likely cause, and the test to validate."},
        @{ Name="SuggestUpsellAsync"; Sig="string cartContents, decimal cartTotal"; Prompt="Suggest 1-2 upsells for this cart: {cartContents} (total {cartTotal}). Justify each with attach rate intuition + margin notes."},
        @{ Name="DraftReturnPolicyAsync"; Sig="string category, string region"; Prompt="Draft a return policy for {category} sold in {region}. Comply with local consumer law, balance customer trust with fraud prevention."}
    )
    "CommerceAccounting" = @(
        @{ Name="ExplainJournalEntryAsync"; Sig="string entryDescription"; Prompt="Translate this transaction into double-entry journal lines: {entryDescription}. Show debits/credits, account codes, narrative."},
        @{ Name="ReconcileVarianceAsync"; Sig="string accountCode, decimal bookBalance, decimal statementBalance"; Prompt="Reconcile {accountCode}: book {bookBalance} vs statement {statementBalance}. List likely variance causes + the journal to fix each."},
        @{ Name="GenerateTrialBalanceCommentaryAsync"; Sig="string period, string topMovements"; Prompt="Comment on the trial balance for {period}. Top movements: {topMovements}. Explain abnormal swings."},
        @{ Name="DraftVatReturnNarrativeAsync"; Sig="string period, decimal outputVat, decimal inputVat"; Prompt="Draft VAT return narrative for {period}: output {outputVat}, input {inputVat}. Cover net payable, anomalies, supporting documents."}
    )
    "CommerceFinance" = @(
        @{ Name="GenerateAgingReportAsync"; Sig="string outstandingInvoices"; Prompt="Generate an aging report from: {outstandingInvoices}. Bucket 0-30/31-60/61-90/90+, name the worst offenders, suggest collection actions."},
        @{ Name="PrepareInvoiceFollowUpAsync"; Sig="string customerName, decimal amount, int daysOverdue"; Prompt="Draft a follow-up message to {customerName} for {amount} due {daysOverdue} days. Tone: firm but relationship-preserving."},
        @{ Name="EvaluateCreditAsync"; Sig="string customerSummary, decimal proposedLimit"; Prompt="Evaluate credit-worthiness of {customerSummary} for a {proposedLimit} limit. Recommend approve/decline + conditions."},
        @{ Name="ForecastCashFlowAsync"; Sig="string outstandingInvoices, string upcomingExpenses, int horizonDays"; Prompt="Forecast cash flow for next {horizonDays} days from invoices: {outstandingInvoices} and expenses: {upcomingExpenses}. Flag squeeze points."}
    )
    "CommerceIntegrationPayFast" = @(
        @{ Name="ExplainItnStatusAsync"; Sig="string itnPayload"; Prompt="Decode this PayFast ITN payload and explain its status: {itnPayload}. Cover payment_status, m_payment_id, signature validity."},
        @{ Name="DraftPayFastBuyButtonAsync"; Sig="string itemName, decimal amount, string returnUrl"; Prompt="Draft a PayFast Buy Button form for '{itemName}' at {amount}, return to {returnUrl}. Include all required fields + signature placeholder."},
        @{ Name="TroubleshootSignatureMismatchAsync"; Sig="string requestParams"; Prompt="Troubleshoot a PayFast signature mismatch. Request params: {requestParams}. List the 5 most common causes + how to verify each."},
        @{ Name="ReconcilePayoutAsync"; Sig="string payoutId, decimal expectedAmount, decimal actualAmount"; Prompt="Reconcile PayFast payout {payoutId}: expected {expectedAmount}, actual {actualAmount}. List likely fee / refund / hold reasons."}
    )
    "CommerceIntegrationXero" = @(
        @{ Name="MapTransactionToXeroAsync"; Sig="string transactionDescription"; Prompt="Map this transaction to a Xero entry: {transactionDescription}. Pick contact, account code, tax rate; output the API payload outline."},
        @{ Name="ResolveXeroErrorAsync"; Sig="string xeroErrorJson"; Prompt="Resolve this Xero API error: {xeroErrorJson}. Explain the root cause + the exact fix (header, scope, validation, etc.)."},
        @{ Name="GenerateXeroReportPromptAsync"; Sig="string reportType, string period"; Prompt="Generate the Xero report request for a {reportType} for {period}. Include endpoint, query params, response fields to surface."},
        @{ Name="MapVatToXeroTaxRateAsync"; Sig="string countryIso, string supplyType"; Prompt="Map this VAT context to the correct Xero tax-rate code: country {countryIso}, supply {supplyType}. Show the code + a one-line justification."}
    )
    "Community" = @(
        @{ Name="WriteAnnouncementAsync"; Sig="string groupName, string subject, string callToAction"; Prompt="Write a community announcement for {groupName} about '{subject}'. CTA: {callToAction}. Warm, concise, 80 words."},
        @{ Name="DraftConflictMediationOpenerAsync"; Sig="string conflictSummary, string partiesInvolved"; Prompt="Draft a mediator-style opener for: {conflictSummary} involving {partiesInvolved}. Acknowledge feelings, set ground rules, propose next step."},
        @{ Name="DesignVolunteerCampaignAsync"; Sig="string need, int peopleNeeded, string when"; Prompt="Design a volunteer drive: need {need}, {peopleNeeded} people, {when}. Cover signup channel, shift design, recognition, retention."},
        @{ Name="WriteCommunityNewsletterAsync"; Sig="string highlights, string upcoming"; Prompt="Write a 200-word community newsletter. Highlights: {highlights}. Upcoming: {upcoming}. Friendly, scan-friendly."}
    )
    "Construction" = @(
        @{ Name="EstimateCostAsync"; Sig="string scope, double areaM2, string finishLevel"; Prompt="Estimate cost for {areaM2}m² of {scope}, finish level {finishLevel}. Break by trade, contingency 10%, exclusions."},
        @{ Name="GenerateSafetyToolboxAsync"; Sig="string activity, string siteHazards"; Prompt="Generate a toolbox talk for '{activity}' with hazards: {siteHazards}. Format: hazards, controls, PPE, sign-off."},
        @{ Name="SequenceCriticalPathAsync"; Sig="string projectScope, int durationDays"; Prompt="Sequence the critical path for: {projectScope} in {durationDays} days. List tasks, dependencies, slack, and 2 risks per phase."},
        @{ Name="DraftSnagListAsync"; Sig="string area, string observations"; Prompt="Draft a snag list for {area}. Observations: {observations}. Order by trade, severity, and access requirement."}
    )
    "Creative" = @(
        @{ Name="GenerateBriefAsync"; Sig="string project, string audience, string deadline"; Prompt="Generate a creative brief for '{project}' aimed at {audience}, due {deadline}. Include problem, success, tone, constraints, deliverables."},
        @{ Name="CritiqueWorkAsync"; Sig="string workDescription, string criteria"; Prompt="Critique this work: {workDescription} against {criteria}. Use 'I notice / I wonder / I suggest', no destructive framing."},
        @{ Name="SuggestStyleReferencesAsync"; Sig="string aesthetic, string medium"; Prompt="Suggest 5 style references for {aesthetic} in {medium}. For each: who/when/why-fits."},
        @{ Name="UnblockCreativeAsync"; Sig="string currentState, string blocker"; Prompt="Help unblock this creative state: {currentState}. Blocker: {blocker}. Offer 3 different reframes + one micro-exercise."}
    )
    "Desktop" = @(
        @{ Name="DiagnoseSlowdownAsync"; Sig="string symptoms, string systemSpecs"; Prompt="Diagnose desktop slowdown: {symptoms} on {systemSpecs}. Top 5 suspect causes + how to verify each in 60 seconds."},
        @{ Name="WriteShortcutCheatsheetAsync"; Sig="string appName, string proficiencyLevel"; Prompt="Write a one-page keyboard shortcut cheatsheet for {appName}, {proficiencyLevel} user. Group by action category."},
        @{ Name="AutomateRepetitiveTaskAsync"; Sig="string taskDescription, string preferredTool"; Prompt="Suggest automation for: {taskDescription} using {preferredTool}. Step-by-step + edge cases."},
        @{ Name="DesignWorkspaceLayoutAsync"; Sig="string monitorCount, string primaryWorkflow"; Prompt="Design a {monitorCount}-monitor workspace layout for: {primaryWorkflow}. Apps per screen, hotkey conventions, eye-line ergonomics."}
    )
    "Education" = @(
        @{ Name="DesignLessonPlanAsync"; Sig="string topic, string gradeBand, int minutes"; Prompt="Design a {minutes}-minute lesson plan on '{topic}' for {gradeBand}. Include objectives, hook, instruction, practice, exit ticket."},
        @{ Name="GenerateAssessmentAsync"; Sig="string topic, string bloomsLevel, int itemCount"; Prompt="Generate {itemCount} assessment items on '{topic}' at Bloom's {bloomsLevel} level. Mix MCQ + short-answer + one performance task."},
        @{ Name="DiagnoseMisconceptionAsync"; Sig="string topic, string studentResponse"; Prompt="Diagnose the misconception in this student response on '{topic}': {studentResponse}. Identify the rule the student is following + a corrective move."},
        @{ Name="DraftParentUpdateAsync"; Sig="string studentName, string period, string progressNotes"; Prompt="Draft a parent update for {studentName} covering {period}. Notes: {progressNotes}. Warm, specific, actionable."}
    )
    "Elderly" = @(
        @{ Name="ReviewMedicationListAsync"; Sig="string medicationList, string conditions"; Prompt="Review this medication list for {conditions}: {medicationList}. Flag potential interactions, redundancies, and timing issues. Defer prescribing to clinician."},
        @{ Name="SuggestFallPreventionAsync"; Sig="string livingArrangement, string mobilityNotes"; Prompt="Suggest fall-prevention measures for {livingArrangement}. Mobility: {mobilityNotes}. Cover home modifications, footwear, exercise, vision."},
        @{ Name="DraftCheckInPromptsAsync"; Sig="string residentName, string interestProfile"; Prompt="Draft 5 warm, dignified check-in conversation prompts for {residentName}. Interests: {interestProfile}. Avoid talk-down language."},
        @{ Name="SummariseCarerHandoverAsync"; Sig="string shiftNotes"; Prompt="Summarise these shift notes for the next carer: {shiftNotes}. SBAR format (Situation, Background, Assessment, Recommendation)."}
    )
    "Energy" = @(
        @{ Name="OptimiseTariffChoiceAsync"; Sig="string usagePattern, string availableTariffs"; Prompt="Recommend the best tariff for usage {usagePattern} from: {availableTariffs}. Show annual cost compare + breakeven assumptions."},
        @{ Name="ExplainBillSpikeAsync"; Sig="string priorBill, string currentBill, string conditions"; Prompt="Explain bill change from {priorBill} to {currentBill}. Conditions: {conditions}. Cover usage, tariff, weather, meter issues."},
        @{ Name="PlanSolarSizingAsync"; Sig="string averageDailyKwh, string roofOrientation, string budget"; Prompt="Size a solar PV system for {averageDailyKwh} kWh/day, {roofOrientation}, budget {budget}. Output panels, inverter, battery, payback years."},
        @{ Name="DraftLoadSheddingPlanAsync"; Sig="string householdSize, string criticalLoads"; Prompt="Draft a load-shedding plan for {householdSize}-person home, critical: {criticalLoads}. Cover backup priority, run-time budget, safety."}
    )
    "Faith" = @(
        @{ Name="ComposeReflectionAsync"; Sig="string tradition, string occasion, string scriptureRef"; Prompt="Compose a 200-word reflection in the {tradition} for {occasion}, anchored in {scriptureRef}. Warm, inclusive, devotional."},
        @{ Name="DraftServiceOrderAsync"; Sig="string tradition, string serviceType, int durationMinutes"; Prompt="Draft a {durationMinutes}-minute {serviceType} order of service in the {tradition}. Sections, transitions, music cues, scripture readings."},
        @{ Name="WritePastoralCareNoteAsync"; Sig="string parishionerSituation"; Prompt="Write a pastoral care note for: {parishionerSituation}. Acknowledge, hold space, offer concrete next step. Avoid platitudes."},
        @{ Name="FindScripturePassagesAsync"; Sig="string tradition, string theme"; Prompt="Find 3 scripture passages on '{theme}' in the {tradition}. For each: reference, key verse text, brief context."}
    )
    "Family" = @(
        @{ Name="PlanFamilyMealsAsync"; Sig="string familySize, string dietaryNotes, int daysCount"; Prompt="Plan {daysCount} days of family meals for {familySize} people, dietary notes: {dietaryNotes}. Include shopping list grouped by aisle."},
        @{ Name="MediateSiblingDisputeAsync"; Sig="string ages, string dispute"; Prompt="Mediate a sibling dispute between ages {ages}: {dispute}. Step-by-step script honouring each child's perspective."},
        @{ Name="DesignHouseholdChoreRotaAsync"; Sig="string members, string chores"; Prompt="Design a fair, age-appropriate chore rota. Members: {members}. Chores: {chores}. Cover frequency and ownership."},
        @{ Name="CelebrateMilestoneAsync"; Sig="string milestone, string memberName, string budget"; Prompt="Plan a {budget} milestone celebration for {memberName}: {milestone}. Ideas across activity / food / memento / message."}
    )
    "Fitness" = @(
        @{ Name="DesignWorkoutPlanAsync"; Sig="string goal, string availableTime, string equipment"; Prompt="Design a workout plan for goal '{goal}', {availableTime} per session, equipment: {equipment}. Periodise over 4 weeks."},
        @{ Name="AnalysePersonalBestProgressionAsync"; Sig="string exercise, string historyJson"; Prompt="Analyse PB progression in {exercise}: {historyJson}. Identify plateaus, recommend deload + next mesocycle target."},
        @{ Name="SuggestRecoveryProtocolAsync"; Sig="string sorenessNotes, string sleepAvgHours"; Prompt="Suggest recovery protocol for soreness: {sorenessNotes}, avg sleep {sleepAvgHours}h. Cover mobility, nutrition, sleep, deload."},
        @{ Name="CritiqueFormCueAsync"; Sig="string exercise, string formDescription"; Prompt="Critique form for {exercise}: {formDescription}. Identify the 2 highest-leverage cues to fix first."}
    )
    "Food" = @(
        @{ Name="SuggestRecipeFromPantryAsync"; Sig="string availableIngredients, string dietNotes"; Prompt="Suggest 3 recipes using mostly: {availableIngredients}. Dietary: {dietNotes}. Pick varied techniques + cuisines."},
        @{ Name="EstimateNutritionAsync"; Sig="string recipeIngredients, int servings"; Prompt="Estimate nutrition per serving for {servings}-serving recipe: {recipeIngredients}. Output kcal, P/F/C, sodium, fibre."},
        @{ Name="SubstituteIngredientAsync"; Sig="string ingredient, string reason"; Prompt="Suggest 3 substitutes for {ingredient} (reason: {reason}). For each: ratio, flavour impact, technique tweak."},
        @{ Name="PlanShoppingListAsync"; Sig="string weeklyMealPlan"; Prompt="Convert this meal plan to a shopping list grouped by store aisle: {weeklyMealPlan}. Aggregate quantities."}
    )
    "Gaming" = @(
        @{ Name="RecommendGameAsync"; Sig="string mood, string platform, int timeAvailableMin"; Prompt="Recommend 3 games for mood '{mood}' on {platform}, with {timeAvailableMin} min. Mix indie/AAA, justify per pick."},
        @{ Name="DesignSpeedrunRouteAsync"; Sig="string gameTitle, string category"; Prompt="Sketch a speedrun route outline for {gameTitle} ({category}). Cover key skips, glitches at high level, risk-vs-reward gates."},
        @{ Name="DraftPatchNotesAsync"; Sig="string changes, string audience"; Prompt="Draft patch notes for changes: {changes}. Audience: {audience}. Group balance/QoL/bugfix, lead with player impact."},
        @{ Name="AnalysePlayerRetentionAsync"; Sig="string day1Pct, string day7Pct, string day30Pct"; Prompt="Analyse retention: D1={day1Pct}, D7={day7Pct}, D30={day30Pct}. Diagnose the weakest curve segment + an experiment to lift it."}
    )
    "Healthcare" = @(
        @{ Name="TriageSymptomsAsync"; Sig="string patientAge, string symptoms, string duration"; Prompt="Triage symptoms for {patientAge}-year-old: {symptoms}, duration {duration}. Output urgency (emergency/urgent/routine), red flags, next step. Defer diagnosis to clinician."},
        @{ Name="ExplainMedicationAsync"; Sig="string medication, string indication"; Prompt="Explain {medication} prescribed for {indication} to a patient. Cover purpose, dose schedule, common side effects, when to call."},
        @{ Name="DraftReferralLetterAsync"; Sig="string fromProvider, string toSpecialty, string clinicalSummary"; Prompt="Draft a referral letter from {fromProvider} to {toSpecialty}. Clinical summary: {clinicalSummary}. Include reason, history, exam, ask."},
        @{ Name="CounselOnAdherenceAsync"; Sig="string medication, string patientConcerns"; Prompt="Counsel on adherence to {medication}. Patient concerns: {patientConcerns}. Address each with evidence + practical strategies."}
    )
    "Home" = @(
        @{ Name="ScheduleMaintenanceAsync"; Sig="string homeAge, string climate"; Prompt="Generate a 12-month home maintenance schedule for a {homeAge}-year-old home in {climate} climate. Monthly tasks + seasonal big-ticket items."},
        @{ Name="DiagnoseHomeIssueAsync"; Sig="string symptom, string location"; Prompt="Diagnose home issue: {symptom} in {location}. List 5 likely causes ranked by probability + a 1-minute check for each."},
        @{ Name="DesignRoomLayoutAsync"; Sig="string roomDimensions, string primaryUse, string furnitureList"; Prompt="Design layout for {roomDimensions} room, primary use: {primaryUse}. Furniture: {furnitureList}. Cover circulation, lighting, focal point."},
        @{ Name="EstimateRenovationCostAsync"; Sig="string scope, string region, string finishLevel"; Prompt="Estimate {finishLevel}-finish renovation cost for: {scope} in {region}. Range with 20% contingency + biggest cost drivers."}
    )
    "Hospitality" = @(
        @{ Name="DraftGuestWelcomeAsync"; Sig="string guestName, string roomType, string lengthOfStay"; Prompt="Draft a warm welcome message for {guestName} in {roomType}, staying {lengthOfStay}. Include wifi, breakfast, local pick."},
        @{ Name="HandleComplaintAsync"; Sig="string complaint, string sentiment"; Prompt="Handle this guest complaint ({sentiment}): {complaint}. Apologise, recover, prevent — concrete next step in each."},
        @{ Name="SuggestExperienceAsync"; Sig="string guestProfile, string lengthOfStay, decimal budget"; Prompt="Suggest a {lengthOfStay} experience for guest: {guestProfile} on {budget} budget. Mix dining, activity, downtime."},
        @{ Name="OptimiseHousekeepingRouteAsync"; Sig="string roomList, int staffCount"; Prompt="Optimise housekeeping route for rooms {roomList} with {staffCount} staff. Sequence for minimum dead-walk + checkout-priority first."}
    )
    "HR" = @(
        @{ Name="DraftJobDescriptionAsync"; Sig="string roleTitle, string seniority, string mustHaves"; Prompt="Draft a job description for {seniority} {roleTitle}. Must-haves: {mustHaves}. Inclusive language, outcomes-led not task-list."},
        @{ Name="StructureInterviewLoopAsync"; Sig="string role, int hoursAvailable"; Prompt="Structure an interview loop for {role} in {hoursAvailable} hours. Map each stage to a competency, name the evaluator role."},
        @{ Name="WritePerformanceFeedbackAsync"; Sig="string employeeName, string strengths, string growthAreas"; Prompt="Write performance feedback for {employeeName}. Strengths: {strengths}. Growth: {growthAreas}. SBI format, specific, future-focused."},
        @{ Name="HandleSensitiveHrIssueAsync"; Sig="string situation, string jurisdiction"; Prompt="Suggest first-response plan for HR situation: {situation} in {jurisdiction}. Cover legal hold, witness, documentation, escalation path."}
    )
    "IoT" = @(
        @{ Name="DiagnoseDeviceOfflineAsync"; Sig="string deviceKind, string lastSeen, string networkType"; Prompt="Diagnose offline {deviceKind} last seen {lastSeen} on {networkType}. List 5 most likely causes ordered by probability + verification step."},
        @{ Name="DesignTelemetrySchemaAsync"; Sig="string deviceClass, string useCase"; Prompt="Design telemetry schema for {deviceClass} used for '{useCase}'. List fields with type, unit, frequency, retention."},
        @{ Name="WriteDeviceCommandAsync"; Sig="string deviceKind, string action, string safetyConstraint"; Prompt="Write a safe device command for {deviceKind} to do '{action}'. Constraint: {safetyConstraint}. Include validation + abort condition."},
        @{ Name="ExplainAnomalyAsync"; Sig="string metric, double observedValue, double expectedBand"; Prompt="Explain {metric} anomaly: observed {observedValue} vs expected {expectedBand}. 3 hypotheses + diagnostic to discriminate."}
    )
    "Kids" = @(
        @{ Name="DesignActivityAsync"; Sig="string ageBand, int minutes, string interests"; Prompt="Design a {minutes}-minute activity for {ageBand} with interests: {interests}. Materials, steps, learning value, mess level."},
        @{ Name="ExplainHardConceptAsync"; Sig="string concept, string ageBand"; Prompt="Explain '{concept}' to {ageBand}. Use one analogy from their world, one example they've seen, one question to check understanding."},
        @{ Name="ScreenContentAsync"; Sig="string contentTitle, string ageBand"; Prompt="Screen '{contentTitle}' for {ageBand}: themes, violence/language/scary moments, talk-after questions, age verdict."},
        @{ Name="HandleBigFeelingAsync"; Sig="string ageBand, string situation"; Prompt="Coach a parent through helping a {ageBand} with big feelings about: {situation}. Validate-name-co-regulate script."}
    )
    "Legal" = @(
        @{ Name="SummariseContractAsync"; Sig="string contractText, string clientRole"; Prompt="Summarise this contract from the {clientRole}'s perspective: {contractText}. Highlight obligations, rights, risks, deadlines."},
        @{ Name="DraftClauseAsync"; Sig="string clauseType, string position, string jurisdiction"; Prompt="Draft a {clauseType} clause favouring the {position} in {jurisdiction}. Plain-English notes alongside."},
        @{ Name="AssessMatterStrengthAsync"; Sig="string matterSummary"; Prompt="Assess this matter's merits: {matterSummary}. Cover liability theory, likely defences, evidence gaps, settlement range. Not legal advice."},
        @{ Name="TrackDeadlineAsync"; Sig="string matterType, string keyDate, string jurisdiction"; Prompt="Identify all deadlines triggered by {keyDate} for a {matterType} matter in {jurisdiction}. List date, action, statute reference."}
    )
    "Logistics" = @(
        @{ Name="OptimiseRouteAsync"; Sig="string origin, string stops, string vehicleConstraints"; Prompt="Optimise delivery route from {origin} through {stops}. Constraints: {vehicleConstraints}. Output sequence, ETAs, total km."},
        @{ Name="DraftCustomsDeclarationAsync"; Sig="string goodsDescription, string fromCountry, string toCountry"; Prompt="Draft a customs declaration outline for: {goodsDescription} from {fromCountry} to {toCountry}. HS code lookup, duty, docs list."},
        @{ Name="DiagnoseDelayAsync"; Sig="string shipmentDetails, string delayCause"; Prompt="Diagnose this shipment delay: {shipmentDetails}, cause: {delayCause}. List recovery options + customer comms template."},
        @{ Name="PlanWarehouseSlottingAsync"; Sig="string skuVelocityList, string warehouseLayout"; Prompt="Plan warehouse slotting for SKUs: {skuVelocityList} in layout: {warehouseLayout}. Optimise for pick-distance + ergonomics."}
    )
    "Media" = @(
        @{ Name="DraftPressReleaseAsync"; Sig="string announcement, string audience"; Prompt="Draft a press release on: {announcement} for {audience}. AP style, inverted pyramid, quote from leadership, boilerplate."},
        @{ Name="SuggestThumbnailConceptsAsync"; Sig="string videoTopic, string channelStyle"; Prompt="Suggest 3 thumbnail concepts for a video on '{videoTopic}' in {channelStyle} style. Hook, composition, text."},
        @{ Name="StructureNarrativeAsync"; Sig="string topic, string format, int durationMinutes"; Prompt="Structure a {durationMinutes}-min {format} on '{topic}'. Hook, beats, payoff, CTA."},
        @{ Name="WriteCaptionAsync"; Sig="string mediaDescription, string platform, string voice"; Prompt="Write a {platform} caption for: {mediaDescription}. Voice: {voice}. Optimise for platform's algorithm + accessibility."}
    )
    "Parenting" = @(
        @{ Name="RespondToBehaviourAsync"; Sig="string childAge, string behaviour, string context"; Prompt="Respond to {childAge}-year-old {behaviour} in context: {context}. Provide a calm script + the developmental rationale."},
        @{ Name="DesignRoutineAsync"; Sig="string childAge, string targetWindow"; Prompt="Design a {targetWindow} routine for a {childAge}-year-old. Cover transitions, sensory needs, choice points."},
        @{ Name="MilestoneCheckInAsync"; Sig="string childAge, string observations"; Prompt="Sanity-check milestones for {childAge}: {observations}. Flag what's normal-range vs worth-discussing-with-pediatrician."},
        @{ Name="PrepareSchoolConferenceAsync"; Sig="string childName, string grade, string concerns"; Prompt="Prepare {childName}'s parent-teacher conference ({grade}). Concerns: {concerns}. Draft questions + advocacy points."}
    )
    "Personal" = @(
        @{ Name="SetWeeklyIntentionsAsync"; Sig="string longTermGoals, string thisWeekContext"; Prompt="Set 3 weekly intentions aligned to: {longTermGoals}. Context this week: {thisWeekContext}. Each: outcome + one daily anchor."},
        @{ Name="DraftDifficultMessageAsync"; Sig="string recipient, string topic, string outcomeWanted"; Prompt="Draft a difficult message to {recipient} about: {topic}. Outcome: {outcomeWanted}. NVC-style: observation, feeling, need, request."},
        @{ Name="DesignRoutineHabitAsync"; Sig="string habit, string currentLifestyle"; Prompt="Design a sustainable routine for habit: {habit}. Current lifestyle: {currentLifestyle}. Cue, action, reward, slip recovery."},
        @{ Name="ReviewWeekAsync"; Sig="string accomplishments, string challenges"; Prompt="Lead a week review. Accomplishments: {accomplishments}. Challenges: {challenges}. Surface insight + one experiment for next week."}
    )
    "PersonalFinance" = @(
        @{ Name="AnalyseSpendingAsync"; Sig="string categoryBreakdown, string monthlyIncome"; Prompt="Analyse spending {categoryBreakdown} against income {monthlyIncome}. Identify 2 leaks + a realistic redirect target."},
        @{ Name="DesignSavingsGoalAsync"; Sig="string goal, decimal targetAmount, int monthsAvailable"; Prompt="Plan to save {targetAmount} for '{goal}' in {monthsAvailable} months. Monthly target + behavioural commitment device."},
        @{ Name="ExplainTaxImpactAsync"; Sig="string scenario, string jurisdiction"; Prompt="Explain tax impact of: {scenario} in {jurisdiction}. Likely treatment, paperwork, optimisation lever. Not tax advice."},
        @{ Name="ReviewInvestmentMixAsync"; Sig="string portfolio, string riskAppetite, int horizonYears"; Prompt="Review investment mix: {portfolio} against {riskAppetite} appetite, {horizonYears}-year horizon. Coverage, concentration, fee drag."}
    )
    "PersonalHealth" = @(
        @{ Name="InterpretVitalsAsync"; Sig="string vitalsJson, string age, string baselineNotes"; Prompt="Interpret vitals {vitalsJson} for age {age}. Baseline: {baselineNotes}. Flag normal/borderline/concerning. Defer diagnosis to clinician."},
        @{ Name="DesignSleepPlanAsync"; Sig="string currentPattern, string targetWakeTime"; Prompt="Design a sleep improvement plan from {currentPattern} towards waking at {targetWakeTime}. Cover light, caffeine, wind-down, environment."},
        @{ Name="PrepareForAppointmentAsync"; Sig="string concern, string appointmentType"; Prompt="Prepare for a {appointmentType} about: {concern}. Pre-visit checklist: symptoms log, questions, medication list, decisions to make."},
        @{ Name="TrackHabitImpactAsync"; Sig="string habit, string vitalsBeforeAfter"; Prompt="Analyse impact of {habit} on vitals: {vitalsBeforeAfter}. Confounders, signal strength, what to keep measuring."}
    )
    "PersonalMental" = @(
        @{ Name="ReframeThoughtAsync"; Sig="string distortedThought, string context"; Prompt="Help reframe this thought: {distortedThought}. Context: {context}. Name the distortion (CBT lens), offer a balanced alternative."},
        @{ Name="DesignCheckInRitualAsync"; Sig="string lifeStage, string availableMinutes"; Prompt="Design a {availableMinutes}-minute daily mental check-in for someone in {lifeStage}. Make it sustainable for low-energy days."},
        @{ Name="PrepareTherapySessionAsync"; Sig="string sessionThemes, string lastWeekEvents"; Prompt="Prepare for a therapy session on themes: {sessionThemes}. Recent events: {lastWeekEvents}. List 3 top topics + one experiment to try."},
        @{ Name="GroundDuringPanicAsync"; Sig="string trigger, string environment"; Prompt="Guide a grounding script for panic triggered by: {trigger} in environment: {environment}. 5-4-3-2-1 sensory anchor + breath."}
    )
    "Pets" = @(
        @{ Name="TriageSymptomAsync"; Sig="string species, string breed, string symptom"; Prompt="Triage pet symptom: {species} ({breed}): {symptom}. Urgency level + immediate vet care advice."},
        @{ Name="CreateTrainingPlanAsync"; Sig="string species, string age, string behaviour"; Prompt="Positive-reinforcement training plan for {age} {species} addressing: {behaviour}. Daily structure, reward strategy, timeline."},
        @{ Name="AdviseDietAsync"; Sig="string species, string lifeStage, string healthNotes"; Prompt="Advise diet for {lifeStage} {species}. Health notes: {healthNotes}. Cover composition, portions, transitions, treats."},
        @{ Name="PlanTravelWithPetAsync"; Sig="string species, string destination, string transport"; Prompt="Plan {transport} travel to {destination} with {species}. Documents, crate, breaks, stress reduction."}
    )
    "RealEstate" = @(
        @{ Name="ValuePropertyAsync"; Sig="string propertyDescription, string suburb, string comparableSales"; Prompt="Estimate value for {propertyDescription} in {suburb}. Comps: {comparableSales}. Range, drivers, market caveats."},
        @{ Name="DraftListingAsync"; Sig="string propertyDescription, string targetBuyer"; Prompt="Draft a property listing for {propertyDescription} targeting {targetBuyer}. Headline, hero paragraph, features, lifestyle close."},
        @{ Name="AnalyseOfferAsync"; Sig="string offerAmount, string listingPrice, string marketConditions"; Prompt="Analyse offer {offerAmount} vs list {listingPrice} in market: {marketConditions}. Counter strategy, negotiation levers."},
        @{ Name="PrepareViewingAsync"; Sig="string propertyType, string targetSegment"; Prompt="Plan an open viewing for {propertyType} aimed at {targetSegment}. Staging, route, FAQs, follow-up cadence."}
    )
    "Relationships" = @(
        @{ Name="PlanCheckInAsync"; Sig="string relationship, string lastTouch, string occasion"; Prompt="Plan a check-in with {relationship}, last touched {lastTouch}. Occasion: {occasion}. Suggest channel, opener, generous question."},
        @{ Name="DraftMeaningfulMessageAsync"; Sig="string recipient, string moment"; Prompt="Draft a heartfelt message to {recipient} for {moment}. Specific, not generic; refer to shared history."},
        @{ Name="ResolveTensionAsync"; Sig="string conflictSummary, string desiredOutcome"; Prompt="Help resolve tension: {conflictSummary}. Desired outcome: {desiredOutcome}. NVC-style script + likely responses."},
        @{ Name="RememberImportantDateAsync"; Sig="string personName, string date, string history"; Prompt="Prep for {personName}'s important date ({date}). History: {history}. Suggest gift, message, gesture."}
    )
    "Retail" = @(
        @{ Name="OptimiseProductMixAsync"; Sig="string topSellersJson, string slowMoversJson"; Prompt="Recommend product mix changes from sellers: {topSellersJson} and slow: {slowMoversJson}. Cover ranging, replenishment, markdown."},
        @{ Name="DesignPromotionAsync"; Sig="string goal, string category, decimal budget"; Prompt="Design a {goal} promotion for {category} on {budget} budget. Mechanic, channel mix, expected lift, guardrails."},
        @{ Name="HandleStockoutAsync"; Sig="string sku, string demandSignal, int leadTimeDays"; Prompt="Handle stockout of {sku} (demand: {demandSignal}, lead {leadTimeDays}d). Recovery options + customer comms."},
        @{ Name="ReviewDailyTradingAsync"; Sig="string salesByCategory, decimal targetRevenue"; Prompt="Review today's trading: {salesByCategory} vs target {targetRevenue}. Wins, misses, tomorrow's adjustments."}
    )
    "Safety" = @(
        @{ Name="ConductRiskAssessmentAsync"; Sig="string activity, string environment"; Prompt="Conduct a risk assessment for {activity} in {environment}. Hazard, likelihood, severity, controls."},
        @{ Name="DraftEmergencyResponseAsync"; Sig="string incidentType, string siteContext"; Prompt="Draft emergency response steps for {incidentType} at {siteContext}. Roles, escalation, comms, debrief."},
        @{ Name="BriefSafetyToolboxAsync"; Sig="string task, string topHazards"; Prompt="Brief a 5-min toolbox talk for task: {task}. Top hazards: {topHazards}. Controls, PPE, sign-off."},
        @{ Name="ReviewIncidentReportAsync"; Sig="string incidentNarrative"; Prompt="Review this incident narrative: {incidentNarrative}. Identify root cause, contributing factors, corrective + preventive actions."}
    )
    "SafetyChild" = @(
        @{ Name="DesignSafetyConversationAsync"; Sig="string childAge, string topic"; Prompt="Design an age-appropriate safety conversation for {childAge} on: {topic}. Concrete examples, scripts they can use, role-play prompt."},
        @{ Name="AssessOnlineRiskAsync"; Sig="string platform, string childAge, string behaviour"; Prompt="Assess online risk on {platform} for {childAge}-year-old showing {behaviour}. Specific risks + parent-action checklist."},
        @{ Name="VerifyTrustedAdultsAsync"; Sig="string contactList"; Prompt="Help vet trusted-adult ring from: {contactList}. Criteria to apply, questions to ask the child."},
        @{ Name="DraftSchoolNotificationAsync"; Sig="string concern, string evidence"; Prompt="Draft a school notification about: {concern}. Evidence: {evidence}. Calm, factual, requesting specific action."}
    )
    "Social" = @(
        @{ Name="DraftPostAsync"; Sig="string topic, string platform, string voice"; Prompt="Draft a {platform} post on '{topic}' in {voice} voice. Hook, payload, CTA, platform-appropriate length."},
        @{ Name="AnalyseEngagementAsync"; Sig="string postPerformance, string baseline"; Prompt="Analyse post performance: {postPerformance} vs baseline: {baseline}. Why it over/under-performed + what to try next."},
        @{ Name="ResponseToCriticAsync"; Sig="string critique, string ourPosition"; Prompt="Respond to public critique: {critique}. Our position: {ourPosition}. De-escalate, acknowledge, offer path forward."},
        @{ Name="DesignContentSeriesAsync"; Sig="string theme, int episodeCount, string platform"; Prompt="Design a {episodeCount}-episode content series on '{theme}' for {platform}. Per-episode hook + cumulative arc."}
    )
    "Sports" = @(
        @{ Name="DesignTrainingBlockAsync"; Sig="string sport, string targetEvent, int weeks"; Prompt="Design a {weeks}-week training block for {sport} peaking at {targetEvent}. Periodisation, key sessions, tapers."},
        @{ Name="AnalysePerformanceAsync"; Sig="string sport, string recentResults, string keyMetrics"; Prompt="Analyse recent {sport} performance: {recentResults}. Key metrics: {keyMetrics}. Strengths to lean into, gaps to close."},
        @{ Name="PlanRecoveryAsync"; Sig="string sessionIntensity, string daysUntilNext"; Prompt="Plan recovery between sessions: {sessionIntensity}, {daysUntilNext} days. Nutrition, sleep, mobility, modality picks."},
        @{ Name="DraftPostMatchReportAsync"; Sig="string match, string keyMoments"; Prompt="Draft a post-match report on {match}. Key moments: {keyMoments}. Tactical, individual standouts, areas to drill."}
    )
    "Tourism" = @(
        @{ Name="BuildItineraryAsync"; Sig="string destination, int days, string travelerProfile"; Prompt="Build a {days}-day {destination} itinerary for {travelerProfile}. Day-by-day rhythm, must-sees, hidden gems, food."},
        @{ Name="EstimateBudgetAsync"; Sig="string destination, int travellers, int days, string standard"; Prompt="Estimate budget for {travellers} pax, {days} days in {destination}, {standard} standard. Categories + total range."},
        @{ Name="HandleTravelDisruptionAsync"; Sig="string disruption, string itineraryContext"; Prompt="Handle travel disruption: {disruption}. Itinerary context: {itineraryContext}. Recovery options, comms templates, rebook checklist."},
        @{ Name="RecommendExperienceAsync"; Sig="string interests, string timeOfDay, string location"; Prompt="Recommend an experience for {interests} at {timeOfDay} in {location}. Why-it-fits + booking practicalities."}
    )
    "Travel" = @(
        @{ Name="OptimiseTripAsync"; Sig="string origin, string destinations, string constraints"; Prompt="Optimise trip from {origin} through {destinations}. Constraints: {constraints}. Route, mode mix, lodging, pace."},
        @{ Name="DraftExpenseClaimAsync"; Sig="string tripSummary, string expenses"; Prompt="Draft expense claim for trip: {tripSummary}. Items: {expenses}. Categorise per company policy, flag missing receipts."},
        @{ Name="PackingListAsync"; Sig="string destination, int days, string activities"; Prompt="Generate packing list for {days} days in {destination}, activities: {activities}. By category + weight optimisation."},
        @{ Name="HandleVisaQueryAsync"; Sig="string fromCountry, string toCountry, string travelPurpose"; Prompt="Outline visa requirements: {fromCountry} → {toCountry} for {travelPurpose}. Process, documents, timeline, common pitfalls."}
    )
    "Wearable" = @(
        @{ Name="InterpretReadingsAsync"; Sig="string metric, string sampleData, string baseline"; Prompt="Interpret wearable {metric} from samples: {sampleData} vs baseline: {baseline}. Signal vs noise, what to do."},
        @{ Name="CorrelateWithBehaviourAsync"; Sig="string metric, string behaviourLog"; Prompt="Correlate {metric} trend with behaviour log: {behaviourLog}. Hypotheses + experiment to test the strongest one."},
        @{ Name="SuggestTrackingExperimentAsync"; Sig="string goal, string availableMetrics"; Prompt="Suggest a 2-week tracking experiment for goal '{goal}' using metrics: {availableMetrics}. Protocol + success criteria."},
        @{ Name="ExplainBatterySavingsAsync"; Sig="string deviceModel, string currentBatteryPct, string usagePattern"; Prompt="Suggest battery savings for {deviceModel} at {currentBatteryPct}% with usage: {usagePattern}. Ranked by impact."}
    )
    "Web" = @(
        @{ Name="AuditPagePerformanceAsync"; Sig="string url, string metricsJson"; Prompt="Audit {url} performance: {metricsJson}. CLS/LCP/TBT issues, top 3 fixes, expected impact."},
        @{ Name="DesignInformationArchitectureAsync"; Sig="string siteGoal, string audience"; Prompt="Design top-level IA for site goal: {siteGoal}, audience: {audience}. Navigation, depth, naming conventions."},
        @{ Name="DraftMetaContentAsync"; Sig="string pageTopic, string primaryKeyword"; Prompt="Draft SEO meta for page on '{pageTopic}' targeting '{primaryKeyword}'. Title (60 char), description (160 char), 3 H1 alternatives."},
        @{ Name="DiagnoseRoutingErrorAsync"; Sig="string url, string statusCode, string serverLogSnippet"; Prompt="Diagnose {statusCode} at {url}. Server log: {serverLogSnippet}. Likely cause, verification step, fix."}
    )
}

$updated = 0
$missing = @()

foreach ($f in $adapters) {
    $vertical = $f.BaseName.Replace("CompanionAdapter", "")
    if (-not $Bespoke.ContainsKey($vertical)) { $missing += $vertical; continue }

    $content = [System.IO.File]::ReadAllText($f.FullName)

    # Detect field name (same logic as bulk-up).
    $field = $null
    if     ($content.Contains("_inner.AgentAsync")) { $field = "_inner" }
    elseif ($content.Contains("_i.AgentAsync"))     { $field = "_i" }
    if (-not $field) { continue }

    # Strip the generic block (4 methods starting at "    public Task<string> ImpactAssessmentAsync").
    $marker = "    public Task<string> ImpactAssessmentAsync"
    $idx = $content.IndexOf($marker)
    if ($idx -lt 0) { continue }   # not previously bulked or already replaced

    $lastBrace = $content.LastIndexOf("}")
    $head = $content.Substring(0, $idx).TrimEnd()
    # Build replacement block.
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine()
    foreach ($m in $Bespoke[$vertical]) {
        $prompt = $m.Prompt
        # The prompt may reference any signature param via {name}. We keep it literal —
        # the C# interpolated string carries the substitution at runtime.
        [void]$sb.AppendLine("    public Task<string> $($m.Name)($($m.Sig), CancellationToken ct=default)")
        [void]$sb.AppendLine("        => $field.AgentAsync(`$`"$prompt`", ct);")
        [void]$sb.AppendLine()
    }
    $newContent = $head + "`n" + $sb.ToString() + "}`n"
    [System.IO.File]::WriteAllText($f.FullName, $newContent)
    $updated++
}

Write-Output "Bespoked: $updated adapter(s)"
if ($missing.Count -gt 0) { Write-Output "No mapping for: $($missing -join ', ')" }
