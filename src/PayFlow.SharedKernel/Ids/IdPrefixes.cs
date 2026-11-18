namespace PayFlow.SharedKernel.Ids;

/// <summary>Registered public id prefixes (docs/DATABASE.md §1).</summary>
public static class IdPrefixes
{
    public const string User = "usr";
    public const string Merchant = "mer";
    public const string Payment = "pay";
    public const string Refund = "re";
    public const string Reversal = "rv";
    public const string Event = "evt";
    public const string WebhookEndpoint = "we";
    public const string Settlement = "stl";
    public const string Statement = "stmt";
    public const string ReconciliationRun = "rcr";
    public const string ReconciliationException = "rex";
    public const string FeePlan = "fp";
    public const string ApiKey = "key";
}
