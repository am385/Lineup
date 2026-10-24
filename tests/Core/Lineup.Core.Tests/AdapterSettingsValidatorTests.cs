using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies portable adapter settings descriptor and snapshot validation.
/// </summary>
public sealed class AdapterSettingsValidatorTests
{
    /// <summary>Verifies a valid descriptor produces no errors.</summary>
    [Fact]
    public void ValidateDescriptor_ValidDescriptor_ReturnsNoErrors()
    {
        // Arrange
        var descriptor = CreateDescriptor(
            new AdapterSettingDescriptor
            {
                Key = "transport",
                Label = "Transport",
                Kind = AdapterSettingKind.Choice,
                Choices =
                [
                    new AdapterSettingChoice { Value = "http", Label = "HTTP" },
                    new AdapterSettingChoice { Value = "https", Label = "HTTPS" }
                ]
            },
            new AdapterSettingDescriptor
            {
                Key = "port",
                Label = "Port",
                Kind = AdapterSettingKind.Integer,
                Minimum = 1,
                Maximum = 65535,
                VisibleWhen =
                [
                    new AdapterSettingCondition
                    {
                        FieldKey = "transport",
                        Operator = AdapterSettingConditionOperator.Equals,
                        Value = "https"
                    }
                ]
            });

        // Act
        var errors = AdapterSettingsValidator.ValidateDescriptor(descriptor);

        // Assert
        Assert.Empty(errors);
    }

    /// <summary>Verifies duplicate keys and unknown conditional references are rejected.</summary>
    [Fact]
    public void ValidateDescriptor_DuplicateKeysAndInvalidReferences_ReturnsErrors()
    {
        // Arrange
        var descriptor = CreateDescriptor(
            new AdapterSettingDescriptor { Key = "address", Label = "Address", Kind = AdapterSettingKind.Text },
            new AdapterSettingDescriptor
            {
                Key = "address",
                Label = "Duplicate",
                Kind = AdapterSettingKind.Text,
                VisibleWhen =
                [
                    new AdapterSettingCondition
                    {
                        FieldKey = "missing",
                        Operator = AdapterSettingConditionOperator.Equals,
                        Value = "true"
                    }
                ]
            });

        // Act
        var errors = AdapterSettingsValidator.ValidateDescriptor(descriptor);

        // Assert
        Assert.Contains("Field key 'address' is duplicated.", errors);
        Assert.Contains("Field 'address' has a visibility condition referencing unknown field 'missing'.", errors);
    }

    /// <summary>Verifies invalid integer bounds are rejected.</summary>
    [Fact]
    public void ValidateDescriptor_InvalidIntegerBounds_ReturnsErrors()
    {
        // Arrange
        var descriptor = CreateDescriptor(
            new AdapterSettingDescriptor
            {
                Key = "count",
                Label = "Count",
                Kind = AdapterSettingKind.Integer,
                Minimum = 10,
                Maximum = 1
            },
            new AdapterSettingDescriptor
            {
                Key = "name",
                Label = "Name",
                Kind = AdapterSettingKind.Text,
                Minimum = 1
            });

        // Act
        var errors = AdapterSettingsValidator.ValidateDescriptor(descriptor);

        // Assert
        Assert.Contains("Field 'count' has a minimum greater than its maximum.", errors);
        Assert.Contains("Field 'name' defines integer bounds but is not an integer field.", errors);
    }

    /// <summary>Verifies invalid choice definitions are rejected.</summary>
    [Fact]
    public void ValidateDescriptor_InvalidChoices_ReturnsErrors()
    {
        // Arrange
        var descriptor = CreateDescriptor(
            new AdapterSettingDescriptor
            {
                Key = "transport",
                Label = "Transport",
                Kind = AdapterSettingKind.Choice,
                Choices =
                [
                    new AdapterSettingChoice { Value = "http", Label = "HTTP" },
                    new AdapterSettingChoice { Value = "http", Label = "Duplicate" }
                ]
            },
            new AdapterSettingDescriptor
            {
                Key = "name",
                Label = "Name",
                Kind = AdapterSettingKind.Text,
                Choices = [new AdapterSettingChoice { Value = "invalid", Label = "Invalid" }]
            });

        // Act
        var errors = AdapterSettingsValidator.ValidateDescriptor(descriptor);

        // Assert
        Assert.Contains("Choice field 'transport' contains duplicate value 'http'.", errors);
        Assert.Contains("Field 'name' defines choices but is not a choice field.", errors);
    }

    /// <summary>Verifies snapshots cannot expose persisted secret values.</summary>
    [Fact]
    public void ValidateSnapshot_SecretValueIsExposed_ReturnsError()
    {
        // Arrange
        var descriptor = CreateDescriptor(
            new AdapterSettingDescriptor { Key = "token", Label = "Token", Kind = AdapterSettingKind.Secret });
        var snapshot = new AdapterSettingsSnapshot
        {
            AdapterKind = descriptor.AdapterKind,
            Values =
            [
                new AdapterSettingSnapshot
                {
                    Key = "token",
                    HasValue = true,
                    Value = JsonSerializer.SerializeToElement("plaintext")
                }
            ]
        };

        // Act
        var errors = AdapterSettingsValidator.ValidateSnapshot(descriptor, snapshot);

        // Assert
        Assert.Contains("Secret field 'token' must not expose its persisted value.", errors);
    }

    /// <summary>Verifies snapshot keys must be unique, known, and complete.</summary>
    [Fact]
    public void ValidateSnapshot_MissingUnknownAndDuplicateValues_ReturnsErrors()
    {
        // Arrange
        var descriptor = CreateDescriptor(
            new AdapterSettingDescriptor { Key = "address", Label = "Address", Kind = AdapterSettingKind.Text },
            new AdapterSettingDescriptor { Key = "enabled", Label = "Enabled", Kind = AdapterSettingKind.Boolean });
        var snapshot = new AdapterSettingsSnapshot
        {
            AdapterKind = descriptor.AdapterKind,
            Values =
            [
                new AdapterSettingSnapshot { Key = "address", Value = JsonSerializer.SerializeToElement("host") },
                new AdapterSettingSnapshot { Key = "address", Value = JsonSerializer.SerializeToElement("duplicate") },
                new AdapterSettingSnapshot { Key = "unknown", Value = JsonSerializer.SerializeToElement(true) }
            ]
        };

        // Act
        var errors = AdapterSettingsValidator.ValidateSnapshot(descriptor, snapshot);

        // Assert
        Assert.Contains("Snapshot key 'address' is duplicated.", errors);
        Assert.Contains("Snapshot key 'unknown' is not defined by the descriptor.", errors);
        Assert.Contains("Snapshot is missing field 'enabled'.", errors);
    }

    private static AdapterSettingsDescriptor CreateDescriptor(params AdapterSettingDescriptor[] fields) =>
        new()
        {
            AdapterKind = "test",
            Fields = fields
        };
}
