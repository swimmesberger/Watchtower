using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Tenancy.Handlers;

namespace Watchtower.Application.Modules.Tenancy;

/// <summary>JSON serializer context for Tenancy module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StackTemplateDto))]
[JsonSerializable(typeof(TemplateEnvVarDto))]
[JsonSerializable(typeof(TemplateEnvVarInput))]
[JsonSerializable(typeof(TenantDto))]
[JsonSerializable(typeof(ListTemplates.Query), TypeInfoPropertyName = "ListTemplatesQuery")]
[JsonSerializable(typeof(ListTemplates.Response), TypeInfoPropertyName = "ListTemplatesResponse")]
[JsonSerializable(typeof(GetTemplate.Query), TypeInfoPropertyName = "GetTemplateQuery")]
[JsonSerializable(typeof(GetTemplate.Response), TypeInfoPropertyName = "GetTemplateResponse")]
[JsonSerializable(typeof(CreateTemplate.Command), TypeInfoPropertyName = "CreateTemplateCommand")]
[JsonSerializable(typeof(CreateTemplate.Response), TypeInfoPropertyName = "CreateTemplateResponse")]
[JsonSerializable(typeof(UpdateTemplate.Command), TypeInfoPropertyName = "UpdateTemplateCommand")]
[JsonSerializable(typeof(UpdateTemplate.Response), TypeInfoPropertyName = "UpdateTemplateResponse")]
[JsonSerializable(typeof(DeleteTemplate.Command), TypeInfoPropertyName = "DeleteTemplateCommand")]
[JsonSerializable(typeof(DeleteTemplate.Response), TypeInfoPropertyName = "DeleteTemplateResponse")]
[JsonSerializable(typeof(AddTenant.Command), TypeInfoPropertyName = "AddTenantCommand")]
[JsonSerializable(typeof(AddTenant.Response), TypeInfoPropertyName = "AddTenantResponse")]
[JsonSerializable(typeof(ListTenants.Query), TypeInfoPropertyName = "ListTenantsQuery")]
[JsonSerializable(typeof(ListTenants.Response), TypeInfoPropertyName = "ListTenantsResponse")]
[JsonSerializable(typeof(DeployAllTenants.Command), TypeInfoPropertyName = "DeployAllTenantsCommand")]
[JsonSerializable(typeof(DeployAllTenants.Response), TypeInfoPropertyName = "DeployAllTenantsResponse")]
public sealed partial class TenancyJsonContext : JsonSerializerContext;
