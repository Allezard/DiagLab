using System.Collections.Concurrent;

namespace DiagLab.Services;

/// <summary>
/// Резервирование остатков на складе.
/// </summary>
public sealed class InventoryService
{
    private static readonly ConcurrentDictionary<int, int> Stock = new();
    private static readonly object ReserveGate = new();

    static InventoryService()
    {
        for (var i = 1; i <= 500; i++)
        {
            Stock[i] = 1000;
        }
    }

    /// <summary>
    /// Резервирование должно быть атомарным: нельзя допустить,
    /// чтобы два запроса зарезервировали один и тот же последний остаток.
    /// </summary>
    public bool Reserve(int productId, int quantity)
    {
        lock (ReserveGate)
        {
            // Обращение к учётной системе
            Thread.Sleep(15);

            if (!Stock.TryGetValue(productId, out var available) || available < quantity)
            {
                return false;
            }

            Stock[productId] = available - quantity;
            return true;
        }
    }

    public int Available(int productId) => Stock.GetValueOrDefault(productId);
}