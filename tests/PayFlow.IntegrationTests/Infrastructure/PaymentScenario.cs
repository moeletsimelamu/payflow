using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace PayFlow.IntegrationTests.Infrastructure;

/// <summary>A merchant ready to take payments: active, ZAR and USD enabled, with an API client.</summary>
public sealed record PayingMerchant(RegisteredMerchant Merchant, IssuedKey Key, HttpClient Api, DashboardClient Owner) : IDisposable
{
    public string Id => Merchant.Id;

    public void Dispose()
    {
        Api.Dispose();
        Owner.Dispose();
    }
}

public static class PaymentScenario
{
    public static async Task<PayingMerchant> PayingMerchantAsync(PayFlowTestHost host, PayFlowApiFactory? factory = null)
    {
        factory ??= host.Factory;
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin, factory);
        var owner = await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword, factory);
        var key = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);
        return new PayingMerchant(merchant, key, MerchantScenario.ApiClient(factory, key.Secret), owner);
    }

    public static object Body(string amount = "100.00", string currency = "ZAR", string method = "sim_card_success", string? reference = null, string? description = null) =>
        new { amount, currency, paymentMethod = method, merchantReference = reference ?? UniqueReference(), description };

    /// <summary>
    /// A random reference with no digits: random hex occasionally forms a 13+ digit, Luhn-valid run,
    /// which the card-number guard rightly rejects (<c>card_data_not_accepted</c>).
    /// </summary>
    public static string UniqueReference() =>
        "order-" + string.Concat(Guid.NewGuid().ToString("N")[..14].Select(c => char.IsAsciiDigit(c) ? (char)('g' + (c - '0')) : c));

    public static async Task<HttpResponseMessage> CreateAsync(HttpClient api, object body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments") { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await api.SendAsync(request);
    }

    public static async Task<JsonElement> CreateOkAsync(HttpClient api, object body)
    {
        var response = await CreateAsync(api, body, Guid.NewGuid().ToString("N"));
        var json = await MerchantScenario.ReadAsync(response);
        ((int)response.StatusCode).ShouldBe(201, json.ToString());
        return json;
    }

    public static async Task<long> ScalarAsync(PayFlowTestHost host, string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<decimal> DecimalAsync(PayFlowTestHost host, string sql, params (string Name, object Value)[] parameters)
    {
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? 0m : Convert.ToDecimal(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Merchant payable balance (credits − debits) for a merchant public id, straight from the ledger tables.</summary>
    public static Task<decimal> MerchantPayableAsync(PayFlowTestHost host, string merchantPublicId, string currency) =>
        DecimalAsync(host,
            """
            SELECT coalesce(sum(CASE WHEN p.direction = 'Credit' THEN p.amount ELSE -p.amount END), 0)
              FROM ledger.postings p JOIN ledger.accounts a ON a.id = p.account_id
             WHERE a.kind = 'MerchantPayable' AND a.currency = @c AND a.merchant_id = @m
            """,
            ("c", currency), ("m", DecodeMerchant(merchantPublicId)));

    public static Guid DecodeMerchant(string publicId) =>
        PayFlow.SharedKernel.Ids.PublicId.TryParse(publicId, "mer", out var id) ? id.Value : throw new ArgumentException(publicId);

    public static Guid DecodePayment(string publicId) =>
        PayFlow.SharedKernel.Ids.PublicId.TryParse(publicId, "pay", out var id) ? id.Value : throw new ArgumentException(publicId);
}
