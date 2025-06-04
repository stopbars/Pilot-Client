# Contributing to BARS Pilot Client

Thank you for your interest in contributing to the BARS Pilot Client! This guide will help you get started with contributing to this C# WPF application.

## Getting Started

### Prerequisites

- Visual Studio 2019 or higher (Community Edition is fine)
- .NET 8.0 SDK
- Git
- Basic knowledge of C# and WPF

### Development Setup

1. **Fork and Clone**

   ```cmd
   git clone https://github.com/stopbars/Pilot-Client.git
   cd Pilot-Client
   ```

2. **Open Solution**

   Open `BARS-Client-V2.sln` in Visual Studio.

3. **Set Build Configuration**

   - Set the build configuration to `Release` and platform to `Any CPU` for final builds
   - Use `Debug|Any CPU` for development and testing

4. **Build the Application**

   ```cmd
   dotnet build BARS-Client-V2.sln --configuration Release
   ```

   The compiled application will be available in `bin\Release\net8.0-windows\`.

5. **Available Build Configurations**

   - `Debug|Any CPU` - Development build with debug symbols
   - `Release|Any CPU` - Production build optimized

## Development Guidelines

### Code Style

- Follow C# naming conventions (PascalCase for classes, methods, properties)
- Use camelCase for private fields and local variables
- Add XML documentation comments for public methods and classes
- Use meaningful variable and method names
- Keep methods focused and avoid overly long functions
- Follow the existing project structure and organization
- Use proper exception handling and logging
- Follow WPF MVVM patterns where applicable
- Use data binding and commanding appropriately

### Code Guidelines

- Use appropriate logging for debugging and error reporting
- Follow WPF best practices and MVVM design patterns
- Implement proper disposal patterns for WPF resources
- Use async/await patterns for network operations and file I/O
- Handle API calls with proper error checking and user feedback
- Test thoroughly before submitting changes
- Ensure UI remains responsive during long-running operations

### Application Architecture

- Follow the established WPF application structure
- Use Services for business logic and external API communication
- Implement proper data binding for UI elements
- Handle application lifecycle events appropriately
- Ensure proper resource management and cleanup

## Contribution Process

### 1. Find or Create an Issue

- Browse existing issues for bug fixes or feature requests
- Create a new issue for significant changes
- Discuss the approach before starting work

### 2. Create a Feature Branch

```cmd
git checkout -b feature/your-feature-name
REM or
git checkout -b fix/your-bug-fix
```

### 3. Make Your Changes

- Write clean, well-documented C# code
- Test your changes thoroughly
- Update documentation if necessary
- Ensure builds complete without errors or warnings

### 4. Commit Your Changes

```cmd
git add .
git commit -m "Add brief description of your changes"
```

Use clear, descriptive commit messages:

- `feat: add new flight planning functionality`
- `fix: resolve connection timeout issue`
- `docs: update application installation instructions`
- `refactor: improve data service architecture`

### 5. Push and Create Pull Request

```cmd
git push origin feature/your-feature-name
```

Create a pull request with:

- Clear description of changes
- Reference to related issues
- Screenshots for UI changes (if applicable)
- Testing instructions for new features
- Confirmation that application builds and runs correctly

## Testing

### Local Testing

1. Build the application: `dotnet build BARS-Client-V2.sln --configuration Release`
2. Run the application: `dotnet run --project BARS-Client-V2.csproj`
3. Test all affected functionality thoroughly
4. Verify UI responsiveness and proper error handling
5. Check application logs for any errors or warnings

### Build Testing

1. Clean the solution: `dotnet clean BARS-Client-V2.sln`
2. Rebuild in Release mode: `dotnet build BARS-Client-V2.sln --configuration Release`
3. Verify no compiler warnings or errors
4. Test application startup and basic functionality

### Integration Testing

- Test with different application configurations and scenarios
- Verify application behavior during network connectivity issues
- Test UI responsiveness and proper form handling
- Ensure proper resource cleanup and disposal
- Test application startup and shutdown procedures

## Common Issues

### Build Errors

- Ensure all NuGet packages are restored: `dotnet restore BARS-Client-V2.sln`
- Check that .NET 8.0 SDK is installed
- Verify project references are correct
- Clean and rebuild the solution if you encounter cache issues: `dotnet clean && dotnet build`

### Application Startup Issues

- Ensure the application is built for the correct target framework (.NET 8.0)
- Check that all dependencies are present in the output directory
- Verify the application configuration files are correct
- Check Windows Event Viewer for application startup errors

### Runtime Errors

- Review application logs and output in the console
- Use Visual Studio debugger when possible
- Test in different application states and scenarios
- Verify proper exception handling in network operations
- Check for WPF binding errors in the output window

## Getting Help

- **Discord**: [Join the BARS Discord server](https://discord.gg/7EhmtwKWzs) for real-time help
- **GitHub Issues**: [Create an issue](https://github.com/stopbars/Pilot-Client/issues/new) for bugs or feature requests
- **Code Review**: Ask for review on complex changes
- **Documentation**: Refer to WPF and .NET documentation for development guidance

## Recognition

Contributors are recognized in:

- Release notes for significant contributions
- Application credits and documentation

Thank you for helping make the BARS Pilot Client better for the flight simulation community!
