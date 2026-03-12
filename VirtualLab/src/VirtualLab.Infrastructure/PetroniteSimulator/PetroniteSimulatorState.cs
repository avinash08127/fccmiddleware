using System.Collections.Concurrent;

namespace VirtualLab.Infrastructure.PetroniteSimulator;

/// <summary>
/// In-memory state for the Petronite REST/JSON simulator.
/// Thread-safe — all collections use concurrent types.
/// </summary>
public sealed class PetroniteSimulatorState
{
    private readonly ConcurrentDictionary<string, PetroniteOAuthToken> _activeTokens = new();
    private readonly ConcurrentDictionary<string, PetroniteOrder> _orders = new();
    private readonly ConcurrentDictionary<int, PetroniteNozzleAssignment> _nozzleAssignments = new();

    /// <summary>Registered webhook callback URL for transaction.completed events.</summary>
    public string? WebhookCallbackUrl { get; set; }

    /// <summary>Number of active OAuth tokens.</summary>
    public int ActiveTokenCount => _activeTokens.Count;

    /// <summary>Number of orders.</summary>
    public int OrderCount => _orders.Count;

    // -----------------------------------------------------------------------
    // OAuth tokens
    // -----------------------------------------------------------------------

    public PetroniteOAuthToken CreateToken(int expiresInSeconds)
    {
        string tokenValue = $"sim-token-{Guid.NewGuid():N}";
        PetroniteOAuthToken token = new()
        {
            AccessToken = tokenValue,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds),
        };

        _activeTokens[tokenValue] = token;
        return token;
    }

    public bool ValidateToken(string? bearerToken)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return false;
        }

        if (_activeTokens.TryGetValue(bearerToken, out PetroniteOAuthToken? token))
        {
            if (token.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return true;
            }

            // Expired — remove
            _activeTokens.TryRemove(bearerToken, out _);
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Orders (direct-authorize-requests)
    // -----------------------------------------------------------------------

    public PetroniteOrder CreateOrder(PetroniteCreateOrderRequest request)
    {
        PetroniteOrder order = new()
        {
            Id = Guid.NewGuid().ToString("D"),
            PumpNumber = request.PumpNumber,
            NozzleNumber = request.NozzleNumber,
            Amount = request.Amount,
            CustomerName = request.CustomerName,
            CustomerTaxId = request.CustomerTaxId,
            CustomerTaxOffice = request.CustomerTaxOffice,
            Status = PetroniteOrderStatus.Created,
        };

        _orders[order.Id] = order;
        return order;
    }

    public PetroniteOrder? GetOrder(string orderId)
    {
        return _orders.TryGetValue(orderId, out PetroniteOrder? order) ? order : null;
    }

    public bool TryAuthorizeOrder(string orderId, out PetroniteOrder? order)
    {
        if (!_orders.TryGetValue(orderId, out order))
        {
            return false;
        }

        if (order.Status != PetroniteOrderStatus.Created)
        {
            return false;
        }

        // Check nozzle lifted state
        if (_nozzleAssignments.TryGetValue(order.PumpNumber, out PetroniteNozzleAssignment? assignment)
            && !assignment.IsNozzleLifted)
        {
            return false;
        }

        order.Status = PetroniteOrderStatus.Authorized;
        order.AuthorizedAtUtc = DateTimeOffset.UtcNow;
        _orders[orderId] = order;
        return true;
    }

    public bool TryCancelOrder(string orderId, out PetroniteOrder? order)
    {
        if (!_orders.TryGetValue(orderId, out order))
        {
            return false;
        }

        if (order.Status == PetroniteOrderStatus.Completed || order.Status == PetroniteOrderStatus.Cancelled)
        {
            return false;
        }

        order.Status = PetroniteOrderStatus.Cancelled;
        order.CancelledAtUtc = DateTimeOffset.UtcNow;
        _orders[orderId] = order;
        return true;
    }

    public bool TryCompleteOrder(string orderId, out PetroniteOrder? order)
    {
        if (!_orders.TryGetValue(orderId, out order))
        {
            return false;
        }

        if (order.Status != PetroniteOrderStatus.Authorized)
        {
            return false;
        }

        order.Status = PetroniteOrderStatus.Completed;
        order.CompletedAtUtc = DateTimeOffset.UtcNow;
        _orders[orderId] = order;
        return true;
    }

    public IReadOnlyList<PetroniteOrder> GetPendingOrders()
    {
        return _orders.Values
            .Where(o => o.Status == PetroniteOrderStatus.Created || o.Status == PetroniteOrderStatus.Authorized)
            .OrderBy(o => o.CreatedAtUtc)
            .ToList();
    }

    public IReadOnlyList<PetroniteOrder> GetAllOrders()
    {
        return _orders.Values.OrderByDescending(o => o.CreatedAtUtc).ToList();
    }

    // -----------------------------------------------------------------------
    // Nozzle assignments
    // -----------------------------------------------------------------------

    public void SetNozzleAssignment(int pumpNumber, PetroniteNozzleAssignment assignment)
    {
        _nozzleAssignments[pumpNumber] = assignment;
    }

    public IReadOnlyList<PetroniteNozzleAssignment> GetNozzleAssignments()
    {
        return _nozzleAssignments.Values.OrderBy(n => n.PumpNumber).ToList();
    }

    public PetroniteNozzleAssignment? GetNozzleAssignment(int pumpNumber)
    {
        return _nozzleAssignments.TryGetValue(pumpNumber, out PetroniteNozzleAssignment? assignment) ? assignment : null;
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    public void Reset(int pumpCount)
    {
        _activeTokens.Clear();
        _orders.Clear();
        _nozzleAssignments.Clear();
        WebhookCallbackUrl = null;

        for (int i = 1; i <= pumpCount; i++)
        {
            _nozzleAssignments[i] = new PetroniteNozzleAssignment
            {
                PumpNumber = i,
                NozzleNumber = 1,
                ProductCode = i <= 2 ? "UNL95" : "DSL",
                ProductName = i <= 2 ? "Unleaded 95" : "Diesel",
                IsNozzleLifted = false,
            };
        }
    }

    /// <summary>Snapshot of current state for diagnostics.</summary>
    public PetroniteStateSnapshot ToSnapshot()
    {
        return new PetroniteStateSnapshot
        {
            WebhookCallbackUrl = WebhookCallbackUrl,
            ActiveTokenCount = ActiveTokenCount,
            OrderCount = OrderCount,
            Orders = GetAllOrders(),
            NozzleAssignments = GetNozzleAssignments(),
            PendingOrders = GetPendingOrders(),
        };
    }
}

// -----------------------------------------------------------------------
// Supporting types
// -----------------------------------------------------------------------

public enum PetroniteOrderStatus
{
    Created,
    Authorized,
    Completed,
    Cancelled,
}

public sealed record PetroniteOAuthToken
{
    public string AccessToken { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public sealed record PetroniteOrder
{
    public string Id { get; init; } = string.Empty;
    public int PumpNumber { get; init; }
    public int NozzleNumber { get; init; }
    public decimal Amount { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string CustomerTaxId { get; init; } = string.Empty;
    public string CustomerTaxOffice { get; init; } = string.Empty;
    public PetroniteOrderStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AuthorizedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
}

public sealed record PetroniteNozzleAssignment
{
    public int PumpNumber { get; init; }
    public int NozzleNumber { get; init; } = 1;
    public string ProductCode { get; init; } = "UNL95";
    public string ProductName { get; init; } = "Unleaded 95";
    public bool IsNozzleLifted { get; set; }
}

public sealed class PetroniteCreateOrderRequest
{
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; } = 1;
    public decimal Amount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerTaxId { get; set; } = string.Empty;
    public string CustomerTaxOffice { get; set; } = string.Empty;
}

public sealed class PetroniteStateSnapshot
{
    public string? WebhookCallbackUrl { get; init; }
    public int ActiveTokenCount { get; init; }
    public int OrderCount { get; init; }
    public IReadOnlyList<PetroniteOrder> Orders { get; init; } = [];
    public IReadOnlyList<PetroniteNozzleAssignment> NozzleAssignments { get; init; } = [];
    public IReadOnlyList<PetroniteOrder> PendingOrders { get; init; } = [];
}
