using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace RussianMetadata;

public sealed class LibraryConfigurationService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryConfigurationService> _logger;

    public LibraryConfigurationService(
        ILibraryManager libraryManager,
        ILogger<LibraryConfigurationService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public LibraryConfigurationResult Apply() => new(
        CompanionPlugin.Libraries?.Apply() ?? [], Array.Empty<string>());

 }
public sealed record LibraryConfigurationResult(
    IReadOnlyList<string> UpdatedLibraries,
    IReadOnlyList<string> SkippedLibraries);
