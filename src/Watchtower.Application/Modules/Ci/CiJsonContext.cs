using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Ci.Handlers;

namespace Watchtower.Application.Modules.Ci;

/// <summary>JSON serializer context for Ci module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CiRepoDto))]
[JsonSerializable(typeof(CiRunnerStatusDto))]
[JsonSerializable(typeof(CiRegistrySyncDto))]
[JsonSerializable(typeof(CiToolchainDto))]
[JsonSerializable(typeof(CiToolchainProfileDto))]
[JsonSerializable(typeof(CiLinkDto))]
[JsonSerializable(typeof(CiAvailableRepoDto))]
[JsonSerializable(typeof(ListRepos.Query), TypeInfoPropertyName = "ListReposQuery")]
[JsonSerializable(typeof(ListRepos.Response), TypeInfoPropertyName = "ListReposResponse")]
[JsonSerializable(typeof(AddRepo.Command), TypeInfoPropertyName = "AddRepoCommand")]
[JsonSerializable(typeof(AddRepo.Response), TypeInfoPropertyName = "AddRepoResponse")]
[JsonSerializable(typeof(UpdateRepo.Command), TypeInfoPropertyName = "UpdateRepoCommand")]
[JsonSerializable(typeof(UpdateRepo.Response), TypeInfoPropertyName = "UpdateRepoResponse")]
[JsonSerializable(typeof(RemoveRepo.Command), TypeInfoPropertyName = "RemoveRepoCommand")]
[JsonSerializable(typeof(RemoveRepo.Response), TypeInfoPropertyName = "RemoveRepoResponse")]
[JsonSerializable(typeof(GetRunnerStatus.Query), TypeInfoPropertyName = "GetRunnerStatusQuery")]
[JsonSerializable(typeof(GetRunnerStatus.Response), TypeInfoPropertyName = "GetRunnerStatusResponse")]
[JsonSerializable(typeof(ListAvailableRepos.Query), TypeInfoPropertyName = "ListAvailableReposQuery")]
[JsonSerializable(typeof(ListAvailableRepos.Response), TypeInfoPropertyName = "ListAvailableReposResponse")]
[JsonSerializable(typeof(GetProductCi.Query), TypeInfoPropertyName = "GetProductCiQuery")]
[JsonSerializable(typeof(GetProductCi.Response), TypeInfoPropertyName = "GetProductCiResponse")]
[JsonSerializable(typeof(EnableForProduct.Command), TypeInfoPropertyName = "EnableForProductCommand")]
[JsonSerializable(typeof(EnableForProduct.Response), TypeInfoPropertyName = "EnableForProductResponse")]
[JsonSerializable(typeof(GetStackCi.Query), TypeInfoPropertyName = "GetStackCiQuery")]
[JsonSerializable(typeof(GetStackCi.Response), TypeInfoPropertyName = "GetStackCiResponse")]
[JsonSerializable(typeof(EnableForStack.Command), TypeInfoPropertyName = "EnableForStackCommand")]
[JsonSerializable(typeof(EnableForStack.Response), TypeInfoPropertyName = "EnableForStackResponse")]
public sealed partial class CiJsonContext : JsonSerializerContext;
