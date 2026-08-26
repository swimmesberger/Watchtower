using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Products.Handlers;

namespace Watchtower.Application.Modules.Products;

/// <summary>JSON serializer context for Products module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProductDto))]
[JsonSerializable(typeof(ProductStackDto))]
[JsonSerializable(typeof(ProductTemplateDto))]
[JsonSerializable(typeof(ProductReleaseSummaryDto))]
[JsonSerializable(typeof(ProductReleaseRefDto))]
[JsonSerializable(typeof(ReleaseRolloutDto))]
[JsonSerializable(typeof(ReleaseRolloutStackDto))]
[JsonSerializable(typeof(ReleaseDto))]
[JsonSerializable(typeof(ReleaseImageDto))]
[JsonSerializable(typeof(ReleaseDetailDto))]
[JsonSerializable(typeof(ListProducts.Query), TypeInfoPropertyName = "ListProductsQuery")]
[JsonSerializable(typeof(ListProducts.Response), TypeInfoPropertyName = "ListProductsResponse")]
[JsonSerializable(typeof(GetProduct.Query), TypeInfoPropertyName = "GetProductQuery")]
[JsonSerializable(typeof(GetProduct.Response), TypeInfoPropertyName = "GetProductResponse")]
[JsonSerializable(typeof(CreateProduct.Command), TypeInfoPropertyName = "CreateProductCommand")]
[JsonSerializable(typeof(CreateProduct.Response), TypeInfoPropertyName = "CreateProductResponse")]
[JsonSerializable(typeof(UpdateProduct.Command), TypeInfoPropertyName = "UpdateProductCommand")]
[JsonSerializable(typeof(UpdateProduct.Response), TypeInfoPropertyName = "UpdateProductResponse")]
[JsonSerializable(typeof(DeleteProduct.Command), TypeInfoPropertyName = "DeleteProductCommand")]
[JsonSerializable(typeof(DeleteProduct.Response), TypeInfoPropertyName = "DeleteProductResponse")]
[JsonSerializable(typeof(ListReleases.Query), TypeInfoPropertyName = "ListReleasesQuery")]
[JsonSerializable(typeof(ListReleases.Response), TypeInfoPropertyName = "ListReleasesResponse")]
[JsonSerializable(typeof(GetRelease.Query), TypeInfoPropertyName = "GetReleaseQuery")]
[JsonSerializable(typeof(GetRelease.Response), TypeInfoPropertyName = "GetReleaseResponse")]
[JsonSerializable(typeof(CreateRelease.Command), TypeInfoPropertyName = "CreateReleaseCommand")]
[JsonSerializable(typeof(CreateRelease.Response), TypeInfoPropertyName = "CreateReleaseResponse")]
[JsonSerializable(typeof(DeleteRelease.Command), TypeInfoPropertyName = "DeleteReleaseCommand")]
[JsonSerializable(typeof(DeleteRelease.Response), TypeInfoPropertyName = "DeleteReleaseResponse")]
[JsonSerializable(typeof(DeployRelease.Command), TypeInfoPropertyName = "DeployReleaseCommand")]
[JsonSerializable(typeof(DeployRelease.Response), TypeInfoPropertyName = "DeployReleaseResponse")]
[JsonSerializable(typeof(GetReleaseRollout.Query), TypeInfoPropertyName = "GetReleaseRolloutQuery")]
[JsonSerializable(typeof(GetReleaseRollout.Response), TypeInfoPropertyName = "GetReleaseRolloutResponse")]
[JsonSerializable(typeof(RetryFailedRollout.Command), TypeInfoPropertyName = "RetryFailedRolloutCommand")]
[JsonSerializable(typeof(RetryFailedRollout.Response), TypeInfoPropertyName = "RetryFailedRolloutResponse")]
[JsonSerializable(typeof(RotateReleaseToken.Command), TypeInfoPropertyName = "RotateReleaseTokenCommand")]
[JsonSerializable(typeof(RotateReleaseToken.Response), TypeInfoPropertyName = "RotateReleaseTokenResponse")]
[JsonSerializable(typeof(SetReleaseWebhook.Command), TypeInfoPropertyName = "SetReleaseWebhookCommand")]
[JsonSerializable(typeof(SetReleaseWebhook.Response), TypeInfoPropertyName = "SetReleaseWebhookResponse")]
public sealed partial class ProductsJsonContext : JsonSerializerContext;
