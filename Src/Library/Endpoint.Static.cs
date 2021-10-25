﻿using FastEndpoints.Validation;
using FastEndpoints.Validation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FastEndpoints;

public abstract partial class Endpoint<TRequest, TResponse> : BaseEndpoint where TRequest : notnull, new() where TResponse : notnull, new()
{
    private static async Task<TRequest> BindToModelAsync(HttpContext ctx, List<ValidationFailure> failures, CancellationToken cancellation)
    {
        TRequest? req = default;

        if (ctx.Request.HasJsonContentType())
            req = await ctx.Request.ReadFromJsonAsync<TRequest>(SerializerOptions, cancellation).ConfigureAwait(false);

        if (req is null) req = new();

        BindFromFormValues(req, ctx.Request, failures);
        BindFromRouteValues(req, ctx.Request.RouteValues, failures);
        BindFromQueryParams(req, ctx.Request.Query, failures);
        BindFromUserClaims(req, ctx, failures);

        if (failures.Count > 0) throw new ValidationFailureException();

        return req;
    }

    private static async Task ValidateRequestAsync(TRequest req, IValidator<TRequest>? validator, HttpContext ctx, object? preProcessors, List<ValidationFailure> validationFailures, CancellationToken cancellation)
    {
        if (validator is null) return;

        var valResult = await validator.ValidateAsync(req, cancellation).ConfigureAwait(false);

        if (!valResult.IsValid)
            validationFailures.AddRange(valResult.Errors);

        if (validationFailures.Count > 0 && ((IValidatorWithState)validator).ThrowIfValidationFails)
        {
            await RunPreprocessors(preProcessors, req, ctx, validationFailures, cancellation).ConfigureAwait(false);
            throw new ValidationFailureException();
        }
    }

    private static async Task RunPostProcessors(object? postProcessors, TRequest req, TResponse resp, HttpContext ctx, List<ValidationFailure> validationFailures, CancellationToken cancellation)
    {
        if (postProcessors is not null)
        {
            foreach (var pp in (IPostProcessor<TRequest, TResponse>[])postProcessors)
                await pp.PostProcessAsync(req, resp, ctx, validationFailures, cancellation).ConfigureAwait(false);
        }
    }

    private static async Task RunPreprocessors(object? preProcessors, TRequest req, HttpContext ctx, List<ValidationFailure> validationFailures, CancellationToken cancellation)
    {
        if (preProcessors is not null)
        {
            foreach (var p in (IPreProcessor<TRequest>[])preProcessors)
                await p.PreProcessAsync(req, ctx, validationFailures, cancellation).ConfigureAwait(false);
        }
    }

    private static void BindFromFormValues(TRequest req, HttpRequest httpRequest, List<ValidationFailure> failures)
    {
        if (!httpRequest.HasFormContentType) return;

        var formFields = httpRequest.Form.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value[0])).ToArray();

        for (int x = 0; x < formFields.Length; x++)
            Bind(req, formFields[x], failures);

        for (int y = 0; y < httpRequest.Form.Files.Count; y++)
        {
            if (ReqTypeCache<TRequest>.CachedProps.TryGetValue(httpRequest.Form.Files[y].Name.ToLower(), out var prop))
            {
                if (prop.PropType == typeof(IFormFile))
                    prop.PropSetter(req, httpRequest.Form.Files[y]);
                else
                    failures.Add(new(prop.PropName, "Files can only be bound to properties of type IFormFile!"));
            }
        }
    }

    private static void BindFromRouteValues(TRequest req, RouteValueDictionary routeValues, List<ValidationFailure> failures)
    {
        var routeKVPs = routeValues.Where(rv => ((string?)rv.Value)?.StartsWith("{") == false).ToArray();

        for (int i = 0; i < routeKVPs.Length; i++)
            Bind(req, routeKVPs[i], failures);
    }

    private static void BindFromQueryParams(TRequest req, IQueryCollection query, List<ValidationFailure> failures)
    {
        var queryParams = query.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value[0])).ToArray();

        for (int i = 0; i < queryParams.Length; i++)
            Bind(req, queryParams[i], failures);
    }

    private static void BindFromUserClaims(TRequest req, HttpContext ctx, List<ValidationFailure> failures)
    {
        for (int i = 0; i < ReqTypeCache<TRequest>.CachedFromClaimProps.Count; i++)
        {
            var (claimType, forbidIfMissing, propSetter) = ReqTypeCache<TRequest>.CachedFromClaimProps[i];
            var claimVal = ctx.User.FindFirst(c => c.Type.Equals(claimType, StringComparison.OrdinalIgnoreCase))?.Value;

            if (claimVal is null && forbidIfMissing)
                failures.Add(new(claimType, "User doesn't have this claim type!"));

            if (claimVal is not null)
                propSetter(req, claimVal);
        }
    }

    private static void Bind(TRequest req, KeyValuePair<string, object?> rv, List<ValidationFailure> failures)
    {
        if (ReqTypeCache<TRequest>.CachedProps.TryGetValue(rv.Key.ToLower(), out var prop))
        {
            bool success = false;
            string propType = string.Empty;

            switch (prop.PropTypeCode)
            {
#pragma warning disable CS8604
                case TypeCode.String:
                    propType = "String";
                    success = true;
                    prop.PropSetter(req, rv.Value);
                    break;
#pragma warning restore CS8604

                case TypeCode.Boolean:
                    propType = "Bool";
                    success = bool.TryParse((string?)rv.Value, out var resBool);
                    prop.PropSetter(req, resBool);
                    break;

                case TypeCode.Int32:
                    propType = "Int";
                    success = int.TryParse((string?)rv.Value, out var resInt);
                    prop.PropSetter(req, resInt);
                    break;

                case TypeCode.Int64:
                    propType = "Long";
                    success = long.TryParse((string?)rv.Value, out var resLong);
                    prop.PropSetter(req, resLong);
                    break;

                case TypeCode.Double:
                    propType = "Double";
                    success = double.TryParse((string?)rv.Value, out var resDbl);
                    prop.PropSetter(req, resDbl);
                    break;

                case TypeCode.Decimal:
                    propType = "Decimal";
                    success = decimal.TryParse((string?)rv.Value, out var resDec);
                    prop.PropSetter(req, resDec);
                    break;

                case TypeCode.DateTime:
                    propType = "DateTime";
                    success = DateTime.TryParse((string?)rv.Value, out var resDateTime);
                    prop.PropSetter(req, resDateTime);
                    break;

                case TypeCode.Object:

                    var prpType = prop.PropType;

                    if (prpType == typeof(IFormFile))
                        return; //skip if it's a form file field

                    if (prpType == typeof(Guid))
                    {
                        propType = "Guid";
                        success = Guid.TryParse((string?)rv.Value, out var resGuid);
                        prop.PropSetter(req, resGuid);
                    }

#pragma warning disable CS8604
                    if (prpType == typeof(Enum))
                    {
                        propType = "Enum";
                        success = Enum.TryParse(prpType, (string?)rv.Value, out var resEnum);
                        prop.PropSetter(req, resEnum);
                    }

                    if (prpType == typeof(Uri))
                    {
                        propType = "Uri";
                        success = true;
                        prop.PropSetter(req, new Uri((string?)rv.Value));
                    }

                    if (prpType == typeof(Version))
                    {
                        propType = "Version";
                        success = Version.TryParse((string?)rv.Value, out var resUri);
                        prop.PropSetter(req, resUri);
                    }
#pragma warning restore CS8604

                    if (prpType == typeof(TimeSpan))
                    {
                        propType = "TimeSpan";
                        success = TimeSpan.TryParse((string?)rv.Value, out var resTimeSpan);
                        prop.PropSetter(req, resTimeSpan);
                    }
                    break;
            }

            if (!success)
                failures.Add(new(prop.PropName, $"Unable to bind [{rv.Value}] to a [{propType}] property!"));
        }
    }
}
