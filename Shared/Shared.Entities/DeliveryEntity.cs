using MongoDB.Bson.Serialization.Attributes;
using System.ComponentModel.DataAnnotations;
using Shared.Entities.Base;
using Shared.Entities.Validation;

namespace Shared.Entities;

/// <summary>
/// Represents a delivery entity in the system.
/// Contains Delivery payload information including version, name, and payload data.
/// </summary>
public class DeliveryEntity : BaseEntity
{
    /// <summary>
    /// Gets or sets the payload data.
    /// This contains the actual delivery payload content.
    /// </summary>
    [BsonElement("payload")]
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the schema identifier.
    /// This links the delivery entity to a specific schema.
    /// Note: Required for Delivery entities, optional for Address entities (handled at controller level).
    /// </summary>
    [BsonElement("schemaId")]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public Guid SchemaId { get; set; } = Guid.Empty;
}
