using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using PayFlow.Api.Hosting;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Migrations;
using PayFlow.Modules.Audit;
using PayFlow.Modules.Identity;
using PayFlow.Modules.Merchants;
using PayFlow.SharedKernel.Money;

namespace PayFlow.ArchitectureTests;

/// <summary>
/// Executable versions of the boundary rules in docs/ARCHITECTURE.md §3 and money rule M1.
/// Adding a module means adding its assembly to <see cref="ModuleAssemblies"/>.
/// </summary>
public sealed class ArchitectureRules
{
    private static readonly Assembly SharedKernel = typeof(Money).Assembly;
    private static readonly Assembly Infrastructure = typeof(PayFlowDbContext).Assembly;
    private static readonly Assembly Api = typeof(ApiSetup).Assembly;
    private static readonly Assembly Migrations = typeof(DesignTimeDbContextFactory).Assembly;

    private static readonly Assembly[] ModuleAssemblies =
    [
        typeof(IdentityModule).Assembly,
        typeof(AuditModule).Assembly,
        typeof(MerchantsModule).Assembly,
        typeof(PayFlow.Modules.Ledger.LedgerModule).Assembly,
        typeof(PayFlow.Modules.Payments.PaymentsModule).Assembly,
        typeof(PayFlow.Modules.Refunds.RefundsModule).Assembly,
        typeof(PayFlow.Modules.Webhooks.WebhooksModule).Assembly,
        typeof(PayFlow.Modules.Settlement.SettlementModule).Assembly,
        typeof(PayFlow.Modules.Reconciliation.ReconciliationModule).Assembly,
    ];

    private static IEnumerable<Assembly> AllPayFlowAssemblies =>
        [SharedKernel, Infrastructure, Api, Migrations, .. ModuleAssemblies];

    [Fact]
    public void SharedKernel_depends_on_no_framework_or_PayFlow_layer()
    {
        var result = Types.InAssembly(SharedKernel).ShouldNot()
            .HaveDependencyOnAny("PayFlow.Infrastructure", "PayFlow.Modules", "PayFlow.Api",
                "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_modules_or_host()
    {
        var result = Types.InAssembly(Infrastructure).ShouldNot()
            .HaveDependencyOnAny("PayFlow.Modules", "PayFlow.Api", "PayFlow.Migrations")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Domain_code_has_no_persistence_or_web_dependencies()
    {
        foreach (var module in ModuleAssemblies)
        {
            var result = Types.InAssembly(module).That().ResideInNamespaceContaining(".Domain")
                .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "PayFlow.Infrastructure")
                .GetResult();
            result.IsSuccessful.ShouldBeTrue(Describe(result));
        }
    }

    [Fact]
    public void Modules_reference_each_other_only_through_Contracts()
    {
        foreach (var module in ModuleAssemblies)
        {
            foreach (var other in ModuleAssemblies.Where(a => a != module))
            {
                // Every namespace and root-level type of the other module, except its Contracts.
                var forbidden = other.GetTypes()
                    .Where(t => t.Namespace is not null && !IsCompilerGenerated(t))
                    .Select(t => t.Namespace!.EndsWith(".Contracts", StringComparison.Ordinal) ? null
                        : t.Namespace == other.GetName().Name ? t.FullName : t.Namespace)
                    .OfType<string>()
                    .Distinct()
                    .ToArray();
                if (forbidden.Length == 0)
                {
                    continue;
                }

                var result = Types.InAssembly(module).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
                result.IsSuccessful.ShouldBeTrue($"{module.GetName().Name} -> {other.GetName().Name}: {Describe(result)}");
            }
        }
    }

    [Fact]
    public void Endpoints_never_use_the_DbContext_directly_outside_identity_plumbing()
    {
        // Phase 2 auth endpoints commit audit records through the shared context; from Phase 3 on,
        // business endpoints must call application services. This rule lists the allowed exceptions.
        var allowed = new[] { "PayFlow.Modules.Identity.Endpoints.AuthEndpoints" };
        foreach (var module in ModuleAssemblies)
        {
            var offenders = Types.InAssembly(module).That().ResideInNamespaceContaining(".Endpoints")
                .And().HaveDependencyOnAny(typeof(PayFlowDbContext).FullName)
                .GetTypes()
                .Where(t => !allowed.Contains(t.FullName) && !allowed.Any(a => t.FullName!.StartsWith(a + "+", StringComparison.Ordinal)))
                .Select(t => t.FullName)
                .ToList();
            offenders.ShouldBeEmpty();
        }
    }

    [Fact]
    public void No_floating_point_types_anywhere_in_PayFlow_code()
    {
        var floating = new[] { typeof(float), typeof(double), typeof(Half), typeof(float?), typeof(double?), typeof(Half?) };
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var violations = new List<string>();
        foreach (var type in AllPayFlowAssemblies.SelectMany(a => a.GetTypes()).Where(t => !IsCompilerGenerated(t)))
        {
            violations.AddRange(type.GetFields(flags).Where(f => floating.Contains(f.FieldType)).Select(f => $"{type.FullName}.{f.Name}"));
            violations.AddRange(type.GetProperties(flags).Where(p => floating.Contains(p.PropertyType)).Select(p => $"{type.FullName}.{p.Name}"));
            violations.AddRange(type.GetMethods(flags)
                .Where(m => floating.Contains(m.ReturnType) || m.GetParameters().Any(p => floating.Contains(p.ParameterType)))
                .Select(m => $"{type.FullName}.{m.Name}()"));
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Every_decimal_column_is_numeric_19_4_and_no_column_is_floating_point()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var properties = db.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).ToList();

        properties.Where(p => p.ClrType == typeof(double) || p.ClrType == typeof(float) ||
                              p.ClrType == typeof(double?) || p.ClrType == typeof(float?))
            .Select(p => $"{p.DeclaringType.Name}.{p.Name}")
            .ShouldBeEmpty();

        foreach (var p in properties.Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            // Money is numeric(19,4); the only other decimals are rates (fractions such as 0.029), numeric(7,6).
            var isRate = p.Name.EndsWith("Rate", StringComparison.Ordinal);
            p.GetPrecision().ShouldBe(isRate ? 7 : 19, $"{p.DeclaringType.Name}.{p.Name}");
            p.GetScale().ShouldBe(isRate ? 6 : 4, $"{p.DeclaringType.Name}.{p.Name}");
        }
    }

    [Fact]
    public void Every_module_EF_mapping_is_registered_for_migrations()
    {
        var mappings = ModuleAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IModuleDbModel).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToHashSet();

        DesignTimeDbContextFactory.Modules.Select(m => m.GetType()).ToHashSet().SetEquals(mappings)
            .ShouldBeTrue("DesignTimeDbContextFactory.Modules must list every IModuleDbModel, or migrations will miss tables.");
    }

    [Fact]
    public void Module_tables_live_in_named_schemas_not_public()
    {
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && string.IsNullOrEmpty(e.GetSchema()))
            .Select(e => e.GetTableName())
            .ShouldBeEmpty();
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.Contains('<', StringComparison.Ordinal);

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []);
}
