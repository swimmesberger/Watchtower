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
public sealed partial class ProductsJsonContext : JsonSerializerContext;
