//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

namespace RetroIO.Management.Contracts;

/// <summary>
/// Standard API response wrapper
/// </summary>
public class ApiResponse<T>
{
    /// <summary>
    /// Gets or sets whether the call succeeded.
    /// </summary>
    public bool Success { get; set; }
    /// <summary>
    /// Gets or sets the payload returned by the call.
    /// </summary>
    public T? Data { get; set; }
    /// <summary>
    /// Gets or sets an optional message for the caller.
    /// </summary>
    public string? Message { get; set; }
    /// <summary>
    /// Gets or sets the error details when the call failed.
    /// </summary>
    public ApiError? Error { get; set; }

    /// <summary>
    /// Creates a successful response that carries the given payload.
    /// </summary>
    /// <param name="data">
    /// The payload to return.
    /// </param>
    /// <param name="message">
    /// An optional message for the caller.
    /// </param>
    /// <returns>
    /// A response with <c>Success</c> set to true.
    /// </returns>
    public static ApiResponse<T> Ok(T? data, string? message = null)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data,
            Message = message
        };
    }

    /// <summary>
    /// Creates a failed response that carries an error.
    /// </summary>
    /// <param name="errorMessage">
    /// The error text.
    /// </param>
    /// <param name="errorCode">
    /// The error code, or null to use <c>ERROR</c>.
    /// </param>
    /// <returns>
    /// A response with <c>Success</c> set to false and <c>Error</c> filled in.
    /// </returns>
    public static ApiResponse<T> Fail(string errorMessage, string? errorCode = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Error = new ApiError
            {
                Message = errorMessage,
                Code = errorCode ?? "ERROR"
            }
        };
    }
}

/// <summary>
/// Describes why an API call failed.
/// </summary>
public class ApiError
{
    /// <summary>
    /// Gets or sets the error code.
    /// </summary>
    public string Code { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the error text.
    /// </summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets extra named values about the error, or null when there are none.
    /// </summary>
    public Dictionary<string, object>? Details { get; set; }
}
