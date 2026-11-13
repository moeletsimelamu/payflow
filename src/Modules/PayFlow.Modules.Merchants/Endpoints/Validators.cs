using FluentValidation;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Application;

namespace PayFlow.Modules.Merchants.Endpoints;

internal sealed class MerchantCurrencyRequestValidator : AbstractValidator<MerchantCurrencyRequest>
{
    public MerchantCurrencyRequestValidator()
    {
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.MaxPaymentAmount).NotEmpty().MaximumLength(32);
        RuleFor(r => r.OperatorRefundLimit).MaximumLength(32);
    }
}

internal sealed class RegisterMerchantRequestValidator : AbstractValidator<RegisterMerchantRequest>
{
    public RegisterMerchantRequestValidator()
    {
        RuleFor(r => r.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.TradingName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ContactEmail).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(r => r.CountryCode).NotEmpty().Length(2).Matches("^[A-Za-z]{2}$");
        RuleFor(r => r.Currencies).NotEmpty().WithMessage("Enable at least one currency.")
            .Must(c => c is null || c.Select(x => x.Currency).Distinct(StringComparer.Ordinal).Count() == c.Count)
            .WithMessage("Each currency may appear only once.");
        RuleForEach(r => r.Currencies).SetValidator(new MerchantCurrencyRequestValidator());
        RuleFor(r => r.AdminEmail).MaximumLength(256).EmailAddress().When(r => !string.IsNullOrEmpty(r.AdminEmail));
        RuleFor(r => r.AdminDisplayName).MaximumLength(200);
    }
}

internal sealed class UpdateMerchantProfileRequestValidator : AbstractValidator<UpdateMerchantProfileRequest>
{
    public UpdateMerchantProfileRequestValidator()
    {
        RuleFor(r => r.TradingName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.ContactEmail).NotEmpty().MaximumLength(256).EmailAddress();
    }
}

internal sealed class ChangeMerchantStatusRequestValidator : AbstractValidator<ChangeMerchantStatusRequest>
{
    public ChangeMerchantStatusRequestValidator()
    {
        RuleFor(r => r.Status).IsInEnum();
        RuleFor(r => r.Reason).MaximumLength(500);
    }
}

internal sealed class CurrencyLimitsRequestValidator : AbstractValidator<CurrencyLimitsRequest>
{
    public CurrencyLimitsRequestValidator()
    {
        RuleFor(r => r.MaxPaymentAmount).NotEmpty().MaximumLength(32);
        RuleFor(r => r.OperatorRefundLimit).MaximumLength(32);
    }
}

internal sealed class ChangeFeePlanRequestValidator : AbstractValidator<ChangeFeePlanRequest>
{
    public ChangeFeePlanRequestValidator() => RuleFor(r => r.FeePlanId).NotEmpty().MaximumLength(40);
}

internal sealed class CreateFeePlanRequestValidator : AbstractValidator<CreateFeePlanRequest>
{
    public CreateFeePlanRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
        RuleFor(r => r.PercentageRate).NotEmpty().MaximumLength(12);
        RuleFor(r => r.Terms).NotEmpty();
        RuleForEach(r => r.Terms).ChildRules(t =>
        {
            t.RuleFor(x => x.Currency).NotEmpty().Length(3);
            t.RuleFor(x => x.FixedFee).NotEmpty().MaximumLength(32);
            t.RuleFor(x => x.MinPaymentAmount).NotEmpty().MaximumLength(32);
        });
    }
}

internal sealed class CreateApiKeyRequestValidator : AbstractValidator<CreateApiKeyRequest>
{
    public CreateApiKeyRequestValidator() => RuleFor(r => r.Name).NotEmpty().MaximumLength(100);
}

internal sealed class RotateApiKeyRequestValidator : AbstractValidator<RotateApiKeyRequest>
{
    public RotateApiKeyRequestValidator() => RuleFor(r => r.GracePeriodHours).InclusiveBetween(0, 24 * 7);
}

internal sealed class InviteMemberRequestValidator : AbstractValidator<InviteMemberRequest>
{
    public InviteMemberRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Role).NotEmpty().Must(r => MerchantRoles.All.Contains(r)).WithMessage($"Role must be one of: {string.Join(", ", MerchantRoles.All)}.");
    }
}

internal sealed class ChangeMemberRoleRequestValidator : AbstractValidator<ChangeMemberRoleRequest>
{
    public ChangeMemberRoleRequestValidator() =>
        RuleFor(r => r.Role).NotEmpty().Must(r => MerchantRoles.All.Contains(r)).WithMessage($"Role must be one of: {string.Join(", ", MerchantRoles.All)}.");
}
