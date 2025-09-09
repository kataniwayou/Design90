using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shared.Entities;
using Processor.Base.Models;
using Processor.Base.Services;
using Processor.Base.Interfaces;
using Shared.Services.Interfaces;
using Shared.Services.Models;
using Shared.Models;
using MassTransit;
using Xunit;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shared.Processor.Tests.Services;

/// <summary>
/// Tests for ProcessorService validation flags functionality
/// </summary>
public class ProcessorServiceValidationFlagsTests
{
    private readonly Mock<IActivityExecutor> _mockActivityExecutor;
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<ISchemaValidator> _mockSchemaValidator;
    private readonly Mock<IBus> _mockBus;
    private readonly Mock<ILogger<ProcessorService>> _mockLogger;
    private readonly Mock<IProcessorMetricsLabelService> _mockLabelService;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly ProcessorConfiguration _processorConfig;
    private readonly Shared.Services.Models.SchemaValidationConfiguration _validationConfig;

    public ProcessorServiceValidationFlagsTests()
    {
        _mockActivityExecutor = new Mock<IActivityExecutor>();
        _mockCacheService = new Mock<ICacheService>();
        _mockSchemaValidator = new Mock<ISchemaValidator>();
        _mockBus = new Mock<IBus>();
        _mockLogger = new Mock<ILogger<ProcessorService>>();
        _mockLabelService = new Mock<IProcessorMetricsLabelService>();
        _mockConfiguration = new Mock<IConfiguration>();

        _processorConfig = new ProcessorConfiguration
        {
            Version = "1.0.0",
            Name = "TestProcessor",
            Description = "Test processor for validation flags",
            InputSchemaId = Guid.NewGuid(),
            OutputSchemaId = Guid.NewGuid(),
            Environment = "Test"
        };

        _validationConfig = new Shared.Services.Models.SchemaValidationConfiguration
        {
            EnableInputValidation = true,
            EnableOutputValidation = true
        };
    }

    private ProcessorService CreateProcessorService(Shared.Services.Models.SchemaValidationConfiguration? customValidationConfig = null)
    {
        var validationConfig = customValidationConfig ?? _validationConfig;
        
        return new ProcessorService(
            _mockActivityExecutor.Object,
            _mockCacheService.Object,
            _mockSchemaValidator.Object,
            _mockBus.Object,
            Options.Create(_processorConfig),
            Options.Create(validationConfig),
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockLabelService.Object
        );
    }

    [Fact]
    public async Task ValidateInputDataAsync_WhenInputValidationDisabled_ShouldReturnTrue()
    {
        // Arrange
        var disabledValidationConfig = new Shared.Services.Models.SchemaValidationConfiguration
        {
            EnableInputValidation = false,
            EnableOutputValidation = true
        };
        
        var service = CreateProcessorService(disabledValidationConfig);
        var testData = "{ \"test\": \"data\" }";

        // Act
        var result = await service.ValidateInputDataAsync(testData, _processorConfig.InputSchemaDefinition, false);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateOutputDataAsync_WhenOutputValidationDisabled_ShouldReturnTrue()
    {
        // Arrange
        var disabledValidationConfig = new Shared.Services.Models.SchemaValidationConfiguration
        {
            EnableInputValidation = true,
            EnableOutputValidation = false
        };
        
        var service = CreateProcessorService(disabledValidationConfig);
        var testData = "{ \"test\": \"data\" }";

        // Act
        var result = await service.ValidateOutputDataAsync(testData, _processorConfig.OutputSchemaDefinition, false);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateInputDataAsync_WhenInputValidationEnabled_ShouldCallValidator()
    {
        // Arrange
        var service = CreateProcessorService();
        var testData = "{ \"test\": \"data\" }";
        _processorConfig.InputSchemaDefinition = "{ \"type\": \"object\" }";
        
        _mockSchemaValidator.Setup(x => x.ValidateAsync(testData, _processorConfig.InputSchemaDefinition))
            .ReturnsAsync(true);

        // Act
        var result = await service.ValidateInputDataAsync(testData, _processorConfig.InputSchemaDefinition, true);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(testData, _processorConfig.InputSchemaDefinition), Times.Once);
    }

    [Fact]
    public async Task ValidateOutputDataAsync_WhenOutputValidationEnabled_ShouldCallValidator()
    {
        // Arrange
        var service = CreateProcessorService();
        var testData = "{ \"test\": \"data\" }";
        _processorConfig.OutputSchemaDefinition = "{ \"type\": \"object\" }";

        _mockSchemaValidator.Setup(x => x.ValidateAsync(testData, _processorConfig.OutputSchemaDefinition))
            .ReturnsAsync(true);

        // Act
        var result = await service.ValidateOutputDataAsync(testData, _processorConfig.OutputSchemaDefinition, true);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(testData, _processorConfig.OutputSchemaDefinition), Times.Once);
    }

    [Fact]
    public async Task ValidateInputDataAsync_WithPluginAssignmentModel_ShouldUsePluginSchema()
    {
        // Arrange
        var service = CreateProcessorService();
        var testData = "{ \"test\": \"data\" }";
        var pluginSchemaDefinition = "{ \"type\": \"object\", \"properties\": { \"test\": { \"type\": \"string\" } } }";

        _mockSchemaValidator.Setup(x => x.ValidateAsync(testData, pluginSchemaDefinition))
            .ReturnsAsync(true);

        // Act
        var result = await service.ValidateInputDataAsync(testData, pluginSchemaDefinition, true);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(testData, pluginSchemaDefinition), Times.Once);
    }

    [Fact]
    public async Task ValidateOutputDataAsync_WithPluginAssignmentModel_ShouldUsePluginSchema()
    {
        // Arrange
        var service = CreateProcessorService();
        var testData = "{ \"result\": \"success\" }";
        var pluginSchemaDefinition = "{ \"type\": \"object\", \"properties\": { \"result\": { \"type\": \"string\" } } }";

        _mockSchemaValidator.Setup(x => x.ValidateAsync(testData, pluginSchemaDefinition))
            .ReturnsAsync(true);

        // Act
        var result = await service.ValidateOutputDataAsync(testData, pluginSchemaDefinition, true);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(testData, pluginSchemaDefinition), Times.Once);
    }

    [Theory]
    [InlineData(true, true, true)] // Both validations enabled, schemas match
    [InlineData(false, true, true)] // Input validation disabled, output matches
    [InlineData(true, false, true)] // Output validation disabled, input matches
    [InlineData(false, false, true)] // Both validations disabled
    public void ValidateSchemaIds_WithDifferentValidationFlags_ShouldRespectFlags(
        bool enableInputValidation,
        bool enableOutputValidation,
        bool expectedResult)
    {
        // Arrange
        var validationConfig = new Shared.Services.Models.SchemaValidationConfiguration
        {
            EnableInputValidation = enableInputValidation,
            EnableOutputValidation = enableOutputValidation
        };

        var service = CreateProcessorService(validationConfig);

        var processorEntity = new ProcessorEntity
        {
            Id = Guid.NewGuid(),
            Version = _processorConfig.Version,
            Name = _processorConfig.Name,
            InputSchemaId = _processorConfig.InputSchemaId,
            OutputSchemaId = _processorConfig.OutputSchemaId
        };

        // Use reflection to access private method
        var method = typeof(ProcessorService).GetMethod("ValidateSchemaIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (bool)method!.Invoke(service, new object[] { processorEntity })!;

        // Assert
        Assert.Equal(expectedResult, result);
    }

    [Fact]
    public void ValidateSchemaIds_WhenInputValidationDisabledAndSchemaMismatch_ShouldStillPass()
    {
        // Arrange
        var validationConfig = new Shared.Services.Models.SchemaValidationConfiguration
        {
            EnableInputValidation = false,
            EnableOutputValidation = true
        };

        var service = CreateProcessorService(validationConfig);

        var processorEntity = new ProcessorEntity
        {
            Id = Guid.NewGuid(),
            Version = _processorConfig.Version,
            Name = _processorConfig.Name,
            InputSchemaId = Guid.NewGuid(), // Different from config
            OutputSchemaId = _processorConfig.OutputSchemaId // Same as config
        };

        // Use reflection to access private method
        var method = typeof(ProcessorService).GetMethod("ValidateSchemaIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (bool)method!.Invoke(service, new object[] { processorEntity })!;

        // Assert
        Assert.True(result); // Should pass because input validation is disabled
    }

    [Fact]
    public void ValidateSchemaIds_WhenOutputValidationDisabledAndSchemaMismatch_ShouldStillPass()
    {
        // Arrange
        var validationConfig = new Shared.Services.Models.SchemaValidationConfiguration
        {
            EnableInputValidation = true,
            EnableOutputValidation = false
        };

        var service = CreateProcessorService(validationConfig);

        var processorEntity = new ProcessorEntity
        {
            Id = Guid.NewGuid(),
            Version = _processorConfig.Version,
            Name = _processorConfig.Name,
            InputSchemaId = _processorConfig.InputSchemaId, // Same as config
            OutputSchemaId = Guid.NewGuid() // Different from config
        };

        // Use reflection to access private method
        var method = typeof(ProcessorService).GetMethod("ValidateSchemaIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        var result = (bool)method!.Invoke(service, new object[] { processorEntity })!;

        // Assert
        Assert.True(result); // Should pass because output validation is disabled
    }

    [Fact]
    public async Task ValidateInputDataAsync_WithPluginAssignmentModelDisabled_ShouldReturnTrue()
    {
        // Arrange
        var service = CreateProcessorService();
        var testData = "{ \"test\": \"data\" }";
        var pluginSchemaDefinition = "{ \"type\": \"object\", \"properties\": { \"test\": { \"type\": \"string\" } } }";

        // Act - validation disabled
        var result = await service.ValidateInputDataAsync(testData, pluginSchemaDefinition, false);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ValidateOutputDataAsync_WithPluginAssignmentModelDisabled_ShouldReturnTrue()
    {
        // Arrange
        var service = CreateProcessorService();
        var testData = "{ \"result\": \"success\" }";
        var pluginSchemaDefinition = "{ \"type\": \"object\", \"properties\": { \"result\": { \"type\": \"string\" } } }";

        // Act - validation disabled
        var result = await service.ValidateOutputDataAsync(testData, pluginSchemaDefinition, false);

        // Assert
        Assert.True(result);
        _mockSchemaValidator.Verify(x => x.ValidateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
