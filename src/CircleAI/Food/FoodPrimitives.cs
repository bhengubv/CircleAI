// FoodPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Food;

public sealed record Recipe(string RecipeId, string Title, IReadOnlyList<string> Ingredients, IReadOnlyList<string> Steps, int Servings, int PrepMinutes);
public sealed record MealLog(string LogId, string UserId, string RecipeId, DateTimeOffset AtUtc, int Servings);
public sealed record PantryItem(string PantryItemId, string Name, double Quantity, string Unit, DateTime? BestBefore);

public interface IFoodBoard
{
    void AddRecipe(Recipe r);
    Recipe? GetRecipe(string id);
    IReadOnlyList<Recipe> SearchByIngredient(string ingredient);
    void Log(MealLog m);
    IReadOnlyList<MealLog> LogsSince(string userId, DateTimeOffset since);
    void StockPantry(PantryItem p);
    void Use(string pantryItemId, double quantity);
    IReadOnlyList<PantryItem> Pantry();
    IReadOnlyList<PantryItem> Expiring(DateTime before);
}

public sealed class InMemoryFoodBoard : IFoodBoard
{
    private readonly ConcurrentDictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
    private readonly List<MealLog> _logs = new();
    private readonly ConcurrentDictionary<string, PantryItem> _pantry = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void AddRecipe(Recipe r) { ArgumentNullException.ThrowIfNull(r); _recipes[r.RecipeId] = r; }
    public Recipe? GetRecipe(string id) => _recipes.GetValueOrDefault(id);
    public IReadOnlyList<Recipe> SearchByIngredient(string ingredient)
    {
        if (string.IsNullOrWhiteSpace(ingredient)) throw new ArgumentException("ingredient required");
        return _recipes.Values.Where(r => r.Ingredients.Any(i => i.Contains(ingredient, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
    public void Log(MealLog m) { ArgumentNullException.ThrowIfNull(m); lock (_lock) _logs.Add(m); }
    public IReadOnlyList<MealLog> LogsSince(string userId, DateTimeOffset since)
    { lock (_lock) return _logs.Where(l => l.UserId == userId && l.AtUtc >= since).OrderBy(l => l.AtUtc).ToArray(); }
    public void StockPantry(PantryItem p) { ArgumentNullException.ThrowIfNull(p); _pantry[p.PantryItemId] = p; }
    public void Use(string pantryItemId, double quantity)
    {
        if (!_pantry.TryGetValue(pantryItemId, out var p)) throw new InvalidOperationException($"Unknown pantry item {pantryItemId}");
        _pantry[pantryItemId] = p with { Quantity = Math.Max(0, p.Quantity - quantity) };
    }
    public IReadOnlyList<PantryItem> Pantry() => _pantry.Values.Where(p => p.Quantity > 0).ToArray();
    public IReadOnlyList<PantryItem> Expiring(DateTime before)
        => _pantry.Values.Where(p => p.BestBefore.HasValue && p.BestBefore.Value <= before).OrderBy(p => p.BestBefore).ToArray();
}
