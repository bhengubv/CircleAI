// SafetyDomainContexts.swift
//
// The two safety domain contexts, which were the only pair of the forty-four
// the Swift port did not carry.
//
// Both snippets name an EMERGENCY NUMBER, and that is the reason they are worth
// reading rather than skimming: this is the one domain where the model being
// told the wrong thing about what to do next has a cost measured in something
// other than tokens. The numbers are South African and they are in the snippet
// verbatim, exactly as the reference has them.
//
// Ported from src/CircleAI.Safety/SafetyDomainContext.cs and
// src/CircleAI.Safety.Child/SafetyChildDomainContext.cs.

import Foundation

/// Static domain-context constants for the personal-safety vertical.
public enum SafetyDomainContext {
    public static let systemPromptSnippet =
        "[DOMAIN: Safety] Personal safety and emergency preparedness assistant. Help with home "
        + "security assessments, emergency response plans, first aid guidance (always recommend "
        + "professional training), situational awareness tips, and crisis communication. "
        + "IMPORTANT: For life-threatening emergencies, direct immediately to 10111 (SAPS) or "
        + "10177 (ambulance). Compliance: POPIA, OHS Act."

    public static let complianceFlags: [String] = [
        "POPIA", "OHS_Act", "Emergency_Protocol_10111",
    ]

    public static let suggestedTools: [String] = [
        "emergency_contacts", "document_editor", "map", "web_search",
    ]
}

/// Static domain-context constants for the child-safeguarding vertical.
///
/// Note the compliance flag: this domain carries `POPIA_Children` rather than
/// the plain `POPIA` the other forty-one use — a stricter regime for a stricter
/// subject. Anything matching on the flag string has to know that, and that
/// the Kids domain spells the same idea a third way again.
public enum SafetyChildDomainContext {
    public static let systemPromptSnippet =
        "[DOMAIN: Safety.Child] Child safety and safeguarding assistant for parents and "
        + "educators. Help with online safety education, age-appropriate device rules, "
        + "recognising grooming signs, reporting abuse, and digital literacy. Always prioritise "
        + "child welfare. IMPORTANT: For immediate child safety concerns, contact SAPS (10111) "
        + "or Childline (116). Compliance: Children's Act 38/2005, POPIA (children's data), "
        + "FILMS_PUBLICATIONS_ACT, Cybercrimes Act."

    public static let complianceFlags: [String] = [
        "Childrens_Act_38_2005", "POPIA_Children", "Films_Publications_Act",
        "Cybercrimes_Act", "Emergency_116",
    ]

    public static let suggestedTools: [String] = [
        "parental_controls", "web_search", "document_editor", "reporting_tools",
    ]
}
