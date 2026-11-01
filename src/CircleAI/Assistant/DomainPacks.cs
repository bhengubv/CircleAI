// DomainPacks.cs
//
// The domain packs' framing, reachable by name (OPEN-GAPS A9).
//
// FORTY-FOUR PACKS AND NOT ONE WAS ON ANY PATH. Each module - Pets, Banking, Farming and the
// rest - carries a DomainContext with a framing paragraph and a CompanionAdapter that puts it in
// front of every message, and nothing named either. The capability tiles sent "I have a
// question about my pet" with no framing at all. A tile now names its pack, the chat it opens
// frames each question the way that pack's adapter does, and an ordinary chat is untouched.
//
// AN EXPLICIT TABLE, NOT REFLECTION. A trimmed release build drops types nothing names, so a
// lookup by reflection would find every pack on the desktop and none on the phone. This table
// names them; DomainPacksTests holds it to the *DomainContext types that exist.
//
// Generated from the *DomainContext.cs files; edit here now.

using System;
using System.Collections.Generic;

namespace CircleAI.Assistant;

/// <summary>The domain packs' framing paragraphs, by pack name.</summary>
public static class DomainPacks
{
    /// <summary>Every pack's framing, keyed by its module name ("Pets", "Personal.Finance").</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Accessibility"] = CircleAI.Accessibility.AccessibilityDomainContext.SystemPromptSnippet,
        ["Agriculture"] = CircleAI.Agriculture.AgricultureDomainContext.SystemPromptSnippet,
        ["Beauty"] = CircleAI.Beauty.BeautyDomainContext.SystemPromptSnippet,
        ["Business"] = CircleAI.Business.BusinessDomainContext.SystemPromptSnippet,
        ["Civic"] = CircleAI.Civic.CivicDomainContext.SystemPromptSnippet,
        ["Commerce"] = CircleAI.Commerce.CommerceDomainContext.SystemPromptSnippet,
        ["Commerce.Accounting"] = CircleAI.CommerceAccounting.CommerceAccountingDomainContext.SystemPromptSnippet,
        ["Commerce.Finance"] = CircleAI.CommerceFinance.CommerceFinanceDomainContext.SystemPromptSnippet,
        ["Commerce.Integration.PayFast"] = CircleAI.CommerceIntegrationPayFast.CommerceIntegrationPayFastDomainContext.SystemPromptSnippet,
        ["Commerce.Integration.Xero"] = CircleAI.CommerceIntegrationXero.CommerceIntegrationXeroDomainContext.SystemPromptSnippet,
        ["Community"] = CircleAI.Community.CommunityDomainContext.SystemPromptSnippet,
        ["Construction"] = CircleAI.Construction.ConstructionDomainContext.SystemPromptSnippet,
        ["Creative"] = CircleAI.Creative.CreativeDomainContext.SystemPromptSnippet,
        ["Education"] = CircleAI.Education.EducationDomainContext.SystemPromptSnippet,
        ["Elderly"] = CircleAI.Elderly.ElderlyDomainContext.SystemPromptSnippet,
        ["Energy"] = CircleAI.Energy.EnergyDomainContext.SystemPromptSnippet,
        ["Faith"] = CircleAI.Faith.FaithDomainContext.SystemPromptSnippet,
        ["Family"] = CircleAI.Family.FamilyDomainContext.SystemPromptSnippet,
        ["Fitness"] = CircleAI.Fitness.FitnessDomainContext.SystemPromptSnippet,
        ["Food"] = CircleAI.Food.FoodDomainContext.SystemPromptSnippet,
        ["Gaming"] = CircleAI.Gaming.GamingDomainContext.SystemPromptSnippet,
        ["HR"] = CircleAI.HR.HRDomainContext.SystemPromptSnippet,
        ["Healthcare"] = CircleAI.Healthcare.HealthcareDomainContext.SystemPromptSnippet,
        ["Home"] = CircleAI.Home.HomeDomainContext.SystemPromptSnippet,
        ["Hospitality"] = CircleAI.Hospitality.HospitalityDomainContext.SystemPromptSnippet,
        ["Kids"] = CircleAI.Kids.KidsDomainContext.SystemPromptSnippet,
        ["Legal"] = CircleAI.Legal.LegalDomainContext.SystemPromptSnippet,
        ["Logistics"] = CircleAI.Logistics.LogisticsDomainContext.SystemPromptSnippet,
        ["Media"] = CircleAI.Media.MediaDomainContext.SystemPromptSnippet,
        ["Parenting"] = CircleAI.Parenting.ParentingDomainContext.SystemPromptSnippet,
        ["Personal"] = CircleAI.Personal.PersonalDomainContext.SystemPromptSnippet,
        ["Personal.Finance"] = CircleAI.PersonalFinance.PersonalFinanceDomainContext.SystemPromptSnippet,
        ["Personal.Health"] = CircleAI.PersonalHealth.PersonalHealthDomainContext.SystemPromptSnippet,
        ["Personal.Mental"] = CircleAI.PersonalMental.PersonalMentalDomainContext.SystemPromptSnippet,
        ["Pets"] = CircleAI.Pets.PetsDomainContext.SystemPromptSnippet,
        ["RealEstate"] = CircleAI.RealEstate.RealEstateDomainContext.SystemPromptSnippet,
        ["Relationships"] = CircleAI.Relationships.RelationshipsDomainContext.SystemPromptSnippet,
        ["Retail"] = CircleAI.Retail.RetailDomainContext.SystemPromptSnippet,
        ["Safety"] = CircleAI.Safety.SafetyDomainContext.SystemPromptSnippet,
        ["Safety.Child"] = CircleAI.SafetyChild.SafetyChildDomainContext.SystemPromptSnippet,
        ["Social"] = CircleAI.Social.SocialDomainContext.SystemPromptSnippet,
        ["Sports"] = CircleAI.Sports.SportsDomainContext.SystemPromptSnippet,
        ["Tourism"] = CircleAI.Tourism.TourismDomainContext.SystemPromptSnippet,
        ["Travel"] = CircleAI.Travel.TravelDomainContext.SystemPromptSnippet,
    };

    /// <summary>The framing for a pack, or null for none or an unknown name.</summary>
    public static string? Framing(string? pack) =>
        pack is not null && All.TryGetValue(pack, out var framing) ? framing : null;

    /// <summary>
    /// A question framed the way the pack's own adapter frames a message: the framing, a blank
    /// line, then the question. No pack, the question as it is.
    /// </summary>
    public static string Frame(string? pack, string question) =>
        Framing(pack) is { } framing ? $"{framing}\n\n{question}" : question;
}
