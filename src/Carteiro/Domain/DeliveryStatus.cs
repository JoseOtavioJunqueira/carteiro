namespace Carteiro.Domain;

public enum DeliveryStatus
{
    /// <summary>Created, waiting for its first attempt (or re-queued manually).</summary>
    Pending,

    /// <summary>The receiver answered 2xx. Terminal.</summary>
    Succeeded,

    /// <summary>The last attempt failed and another one is scheduled.</summary>
    Failed,

    /// <summary>Retries are exhausted. Stays here until someone asks for a manual retry.</summary>
    Dead,
}
