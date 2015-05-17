﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RestEase.Implementation
{
    /// <summary>
    /// Helper class used to generate interface implementations. Exposed for testing (and very adventurous people) only.
    /// </summary>
    public class ImplementationBuilder
    {
        private static readonly Regex pathParamMatch = new Regex(@"\{(.+?)\}");

        private static readonly string moduleBuilderName = "RestEaseAutoGeneratedModule";

        private static readonly MethodInfo requestVoidAsyncMethod = typeof(IRequester).GetMethod("RequestVoidAsync");
        private static readonly MethodInfo requestAsyncMethod = typeof(IRequester).GetMethod("RequestAsync");
        private static readonly MethodInfo requestWithResponseMessageAsyncMethod = typeof(IRequester).GetMethod("RequestWithResponseMessageAsync");
        private static readonly MethodInfo requestWithResponseAsyncMethod = typeof(IRequester).GetMethod("RequestWithResponseAsync");
        private static readonly MethodInfo requestRawAsyncMethod = typeof(IRequester).GetMethod("RequestRawAsync");
        private static readonly ConstructorInfo requestInfoCtor = typeof(RequestInfo).GetConstructor(new[] { typeof(HttpMethod), typeof(string) });
        private static readonly MethodInfo cancellationTokenSetter = typeof(RequestInfo).GetProperty("CancellationToken").SetMethod;
        private static readonly MethodInfo allowAnyStatusCodeSetter = typeof(RequestInfo).GetProperty("AllowAnyStatusCode").SetMethod;
        private static readonly MethodInfo addQueryParameterMethod = typeof(RequestInfo).GetMethod("AddQueryParameter");
        private static readonly MethodInfo queryMapSetter = typeof(RequestInfo).GetProperty("QueryMap").SetMethod;
        private static readonly MethodInfo addPathParameterMethod = typeof(RequestInfo).GetMethod("AddPathParameter");
        private static readonly MethodInfo setClassHeadersMethod = typeof(RequestInfo).GetProperty("ClassHeaders").SetMethod;
        private static readonly MethodInfo addPropertyHeaderMethod = typeof(RequestInfo).GetMethod("AddPropertyHeader");
        private static readonly MethodInfo addMethodHeaderMethod = typeof(RequestInfo).GetMethod("AddMethodHeader");
        private static readonly MethodInfo addHeaderParameterMethod = typeof(RequestInfo).GetMethod("AddHeaderParameter");
        private static readonly MethodInfo setBodyParameterInfoMethod = typeof(RequestInfo).GetMethod("SetBodyParameterInfo");
        private static readonly ConstructorInfo listOfKvpOfStringNCtor = typeof(List<KeyValuePair<string, string>>).GetConstructor(new[] { typeof(int) });
        private static readonly MethodInfo listOfKvpOfStringAdd = typeof(List<KeyValuePair<string, string>>).GetMethod("Add");
        private static readonly ConstructorInfo kvpOfStringCtor = typeof(KeyValuePair<string, string>).GetConstructor(new[] { typeof(string), typeof(string) });

        private static readonly Dictionary<HttpMethod, PropertyInfo> httpMethodProperties = new Dictionary<HttpMethod, PropertyInfo>()
        {
            { HttpMethod.Delete, typeof(HttpMethod).GetProperty("Delete") },
            { HttpMethod.Get, typeof(HttpMethod).GetProperty("Get") },
            { HttpMethod.Head, typeof(HttpMethod).GetProperty("Head") },
            { HttpMethod.Options, typeof(HttpMethod).GetProperty("Options") },
            { HttpMethod.Post, typeof(HttpMethod).GetProperty("Post") },
            { HttpMethod.Put, typeof(HttpMethod).GetProperty("Put") },
            { HttpMethod.Trace, typeof(HttpMethod).GetProperty("Trace") }
        };

        private readonly ModuleBuilder moduleBuilder;
        private readonly ConcurrentDictionary<Type, Func<IRequester, object>> creatorCache = new ConcurrentDictionary<Type, Func<IRequester, object>>();

        /// <summary>
        /// Initialises a new instance of the <see cref="ImplementationBuilder"/> class
        /// </summary>
        public ImplementationBuilder()
        {
            var assemblyName = new AssemblyName(RestClient.FactoryAssemblyName);
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleBuilderName);
            this.moduleBuilder = moduleBuilder;
        }

        /// <summary>
        /// Create an implementation of the given interface, using the given requester
        /// </summary>
        /// <typeparam name="T">Type of interface to implement</typeparam>
        /// <param name="requester">Requester to be used by the generated implementation</param>
        /// <returns>An implementation of the given interface</returns>
        public T CreateImplementation<T>(IRequester requester)
        {
            if (requester == null)
                throw new ArgumentNullException("requester");

            var creator = this.creatorCache.GetOrAdd(typeof(T), key =>
            {
                var implementationType = this.BuildImplementationImpl(key);
                return this.BuildCreator(implementationType);
            });

            T implementation = (T)creator(requester);

            return implementation;
        }

        private Func<IRequester, object> BuildCreator(Type implementationType)
        {
            var requesterParam = Expression.Parameter(typeof(IRequester));
            var ctor = Expression.New(implementationType.GetConstructor(new[] { typeof(IRequester) }), requesterParam);
            return Expression.Lambda<Func<IRequester, object>>(ctor, requesterParam).Compile();
        }

        private Type BuildImplementationImpl(Type interfaceType)
        {
            if (!interfaceType.IsInterface)
                throw new ArgumentException(String.Format("Type {0} is not an interface", interfaceType.Name));

            // TODO: This generates messy (but valid) names for generic interfaces
            var typeBuilder = this.moduleBuilder.DefineType(String.Format("RestEase.AutoGenerated.{0}", interfaceType.FullName), TypeAttributes.Public);
            typeBuilder.AddInterfaceImplementation(interfaceType);

            var classHeaders = interfaceType.GetCustomAttributes<HeaderAttribute>().ToArray();
            var firstHeaderWithoutValue = classHeaders.FirstOrDefault(x => x.Value == null);
            if (firstHeaderWithoutValue != null)
                throw new ImplementationCreationException(String.Format("[Header(\"{0}\")] on interface must have the form [Header(\"Name\", \"Value\")]", firstHeaderWithoutValue.Name));
            var firstHeaderWithColon = classHeaders.FirstOrDefault(x => x.Name.Contains(':'));
            if (firstHeaderWithColon != null)
                throw new ImplementationCreationException(String.Format("[Header(\"{0}\", \"{1}\")] on interface must not have a colon in the header name", firstHeaderWithColon.Name, firstHeaderWithColon.Value));

            var classAllowAnyStatusCodeAttribute = interfaceType.GetCustomAttribute<AllowAnyStatusCodeAttribute>();

            // Define a readonly field which holds a reference to the IRequester
            var requesterField = typeBuilder.DefineField("requester", typeof(IRequester), FieldAttributes.Private | FieldAttributes.InitOnly);

            this.AddInstanceCtor(typeBuilder, requesterField);

            // If there are any class headers, define a static readonly field which contains them
            // Also define a static constructor to initialise it
            FieldBuilder classHeadersField = null;
            if (classHeaders.Length > 0)
            {
                classHeadersField = typeBuilder.DefineField("classHeaders", typeof(List<KeyValuePair<string, string>>), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                this.AddStaticCtor(typeBuilder, classHeaders, classHeadersField);
            }

            this.HandleEvents(interfaceType);
            var propertyHeaders = this.HandleProperties(typeBuilder, interfaceType);

            // Time out for a quick sanity check
            var firstDuplicateInterfaceHeader = classHeaders.Select(x => x.Name).Intersect(propertyHeaders.Select(x => x.Key)).FirstOrDefault();
            if (firstDuplicateInterfaceHeader != null)
                throw new ImplementationCreationException(String.Format("[Header(\"{0}\")] exists both on the interface and on a property. You can specify it on one or the other, but not both", firstDuplicateInterfaceHeader));

            this.HandleMethods(typeBuilder, interfaceType, requesterField, classHeadersField, classAllowAnyStatusCodeAttribute, propertyHeaders);

            Type constructedType;
            try
            {
                constructedType = typeBuilder.CreateType();
            }
            catch (TypeLoadException e)
            {
                var msg = String.Format("Unable to create implementation for interface {0}. Ensure that the interface is public, or add [assembly: InternalsVisibleTo(RestClient.FactoryAssemblyName)] to your AssemblyInfo.cs", interfaceType.FullName);
                throw new ImplementationCreationException(msg, e);
            }

            return constructedType;
        }

        private void AddInstanceCtor(TypeBuilder typeBuilder, FieldBuilder requesterField)
        {
            // Add a constructor which takes the IRequester and assigns it to the field
            // public Name(IRequester requester)
            // {
            //     this.requester = requester;
            // }
            var ctorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IRequester) });
            var ctorIlGenerator = ctorBuilder.GetILGenerator();
            // Load 'this' and the requester onto the stack
            ctorIlGenerator.Emit(OpCodes.Ldarg_0);
            ctorIlGenerator.Emit(OpCodes.Ldarg_1);
            // Store the requester into this.requester
            ctorIlGenerator.Emit(OpCodes.Stfld, requesterField);
            ctorIlGenerator.Emit(OpCodes.Ret);
        }

        private void AddStaticCtor(TypeBuilder typeBuilder, HeaderAttribute[] classHeaders, FieldBuilder classHeadersField)
        {
            // static Name()
            // {
            //     classHeaders = new List<KeyValuePair<string>>(n);
            //     classHeaders.Add(new KeyValuePair<string, string>("name", "value"));
            //     ...
            // }

            var staticCtorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Static, CallingConventions.Standard, new Type[0]);
            var staticCtorIlGenerator = staticCtorBuilder.GetILGenerator();

            // Load the list size onto the stack
            // Stack: [list size]
            staticCtorIlGenerator.Emit(OpCodes.Ldc_I4, classHeaders.Length);
            // Ctor the list
            // Stack: [list]
            staticCtorIlGenerator.Emit(OpCodes.Newobj, listOfKvpOfStringNCtor);
            // Load each class header into the list
            foreach (var classHeader in classHeaders)
            {
                staticCtorIlGenerator.Emit(OpCodes.Dup);
                staticCtorIlGenerator.Emit(OpCodes.Ldstr, classHeader.Name);
                staticCtorIlGenerator.Emit(OpCodes.Ldstr, classHeader.Value);
                staticCtorIlGenerator.Emit(OpCodes.Newobj, kvpOfStringCtor);
                staticCtorIlGenerator.Emit(OpCodes.Callvirt, listOfKvpOfStringAdd);
            }
            // Finally, store the list in its static field
            staticCtorIlGenerator.Emit(OpCodes.Stsfld, classHeadersField);
            staticCtorIlGenerator.Emit(OpCodes.Ret);
        }

        private void HandleMethods(
            TypeBuilder typeBuilder,
            Type interfaceType,
            FieldBuilder requesterField,
            FieldInfo classHeadersField,
            AllowAnyStatusCodeAttribute classAllowAnyStatusCodeAttribute,
            List<KeyValuePair<string, FieldBuilder>> propertyHeaders)
        {
            foreach (var methodInfo in interfaceType.GetMethods())
            {
                // Exclude property getter / setters, etc
                if (methodInfo.IsSpecialName)
                    continue;

                var requestAttribute = methodInfo.GetCustomAttribute<RequestAttribute>();
                if (requestAttribute == null)
                    throw new ImplementationCreationException(String.Format("Method {0} does not have a suitable [Get] / [Post] / etc attribute on it", methodInfo.Name));

                var allowAnyStatusCodeAttribute = methodInfo.GetCustomAttribute<AllowAnyStatusCodeAttribute>();

                var parameters = methodInfo.GetParameters();
                var parameterGrouping = new ParameterGrouping(parameters, methodInfo.Name);

                this.ValidatePathParams(requestAttribute.Path, parameterGrouping.PathParameters.Select(x => x.Attribute.Name ?? x.Parameter.Name), methodInfo.Name);

                var methodBuilder = typeBuilder.DefineMethod(methodInfo.Name, MethodAttributes.Public | MethodAttributes.Virtual, methodInfo.ReturnType, parameters.Select(x => x.ParameterType).ToArray());
                var methodIlGenerator = methodBuilder.GetILGenerator();

                this.AddRequestInfoCreation(methodIlGenerator, requesterField, requestAttribute);

                // If there's a cancellationtoken, add that
                if (parameterGrouping.CancellationToken.HasValue)
                {
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldarg, (short)parameterGrouping.CancellationToken.Value.Index);
                    methodIlGenerator.Emit(OpCodes.Callvirt, cancellationTokenSetter);
                }

                // If there are any class headers, add them
                if (classHeadersField != null)
                {
                    // requestInfo.ClassHeaders = classHeaders
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldsfld, classHeadersField);
                    methodIlGenerator.Emit(OpCodes.Callvirt, setClassHeadersMethod);
                }

                // If there are any property headers, add them
                foreach (var propertyHeader in propertyHeaders)
                {
                    var typedMethod = addPropertyHeaderMethod.MakeGenericMethod(propertyHeader.Value.FieldType);
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldstr, propertyHeader.Key);
                    methodIlGenerator.Emit(OpCodes.Ldarg_0);
                    methodIlGenerator.Emit(OpCodes.Ldfld, propertyHeader.Value);
                    methodIlGenerator.Emit(OpCodes.Callvirt, typedMethod);
                }

                // If there are any method headers, add them
                var methodHeaders = methodInfo.GetCustomAttributes<HeaderAttribute>();
                // ... after a quick sanity check
                var firstDuplicateHeader = methodHeaders.Select(x => x.Name).Intersect(parameterGrouping.HeaderParameters.Select(x => x.Attribute.Name)).FirstOrDefault();
                if (firstDuplicateHeader != null)
                    throw new ImplementationCreationException(String.Format("[Header(\"{0}\")] exists both on method {1} and on one of its parameters. You can specify it on one or the other, but not both", firstDuplicateHeader, methodInfo.Name));

                foreach (var methodHeader in methodHeaders)
                {
                    if (methodHeader.Name.Contains(':'))
                        throw new ImplementationCreationException(String.Format("[Header(\"{0}\")] on method {1} must not have colon in its name", methodHeader.Name, methodInfo.Name));
                    this.AddMethodHeader(methodIlGenerator, methodHeader);
                }

                // If we want to allow any status code, set that
                var resolvedAllowAnyStatusAttribute = allowAnyStatusCodeAttribute ?? classAllowAnyStatusCodeAttribute;
                if (resolvedAllowAnyStatusAttribute != null && resolvedAllowAnyStatusAttribute.AllowAnyStatusCode)
                {
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldc_I4_1);
                    methodIlGenerator.Emit(OpCodes.Callvirt, allowAnyStatusCodeSetter);
                }

                this.AddParameters(methodIlGenerator, parameterGrouping, methodInfo.Name);

                this.AddRequestMethodInvocation(methodIlGenerator, methodInfo);

                // Finally, return
                methodIlGenerator.Emit(OpCodes.Ret);

                typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);
            }
        }

        private void HandleEvents(Type interfaceType)
        {
            if (interfaceType.GetEvents().Any())
                throw new ImplementationCreationException("Interface must not have any events");
        }

        private List<KeyValuePair<string, FieldBuilder>> HandleProperties(TypeBuilder typeBuilder, Type interfaceType)
        {
            var propertyHeaderFields = new List<KeyValuePair<string, FieldBuilder>>();
            MethodAttributes attributes = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName;

            foreach (var property in interfaceType.GetProperties())
            {
                var headerAttribute = property.GetCustomAttribute<HeaderAttribute>();
                if (headerAttribute == null)
                    throw new ImplementationCreationException(String.Format("Property {0} does not have a [Header(\"Name\")] attribute", property.Name));

                if (headerAttribute.Value != null)
                    throw new ImplementationCreationException(String.Format("[Header(\"{0}\", \"{1}\")] on property {2} must have the form [Header(\"Name\")], not [Header(\"Name\", \"Value\")]", headerAttribute.Name, headerAttribute.Value, property.Name));
                if (headerAttribute.Name.Contains(':'))
                    throw new ImplementationCreationException(String.Format("[Header(\"{0}\")] on property {1} must not have a colon in its name", headerAttribute.Name, property.Name));

                if (!property.CanRead || !property.CanWrite)
                    throw new ImplementationCreationException(String.Format("Property {0} must have both a getter and a setter", property.Name));

                var propertyBuilder = typeBuilder.DefineProperty(property.Name, PropertyAttributes.None, property.PropertyType, null);
                var getter = typeBuilder.DefineMethod(property.GetMethod.Name, attributes, property.PropertyType, Type.EmptyTypes);
                var setter = typeBuilder.DefineMethod(property.SetMethod.Name, attributes, null, new Type[] { property.PropertyType });
                var backingField = typeBuilder.DefineField("bk_" + property.Name, property.PropertyType, FieldAttributes.Private);

                var getterIlGenerator = getter.GetILGenerator();
                getterIlGenerator.Emit(OpCodes.Ldarg_0);
                getterIlGenerator.Emit(OpCodes.Ldfld, backingField);
                getterIlGenerator.Emit(OpCodes.Ret);
                propertyBuilder.SetGetMethod(getter);

                var setterIlGenerator = setter.GetILGenerator();
                setterIlGenerator.Emit(OpCodes.Ldarg_0);
                setterIlGenerator.Emit(OpCodes.Ldarg_1);
                setterIlGenerator.Emit(OpCodes.Stfld, backingField);
                setterIlGenerator.Emit(OpCodes.Ret);
                propertyBuilder.SetSetMethod(setter);

                propertyHeaderFields.Add(new KeyValuePair<string, FieldBuilder>(headerAttribute.Name, backingField));
            }

            return propertyHeaderFields;
        }

        private void AddRequestInfoCreation(ILGenerator methodIlGenerator, FieldBuilder requesterField, RequestAttribute requestAttribute)
        {
            // Load 'this' onto the stack
            // Stack: [this]
            methodIlGenerator.Emit(OpCodes.Ldarg_0);
            // Load 'this.requester' onto the stack
            // Stack: [this.requester]
            methodIlGenerator.Emit(OpCodes.Ldfld, requesterField);

            // Start loading the ctor params for RequestInfo onto the stack
            // 1. HttpMethod
            // Stack: [this.requester, HttpMethod]
            methodIlGenerator.Emit(OpCodes.Call, httpMethodProperties[requestAttribute.Method].GetMethod);
            // 2. The Path
            // Stack: [this.requester, HttpMethod, path]
            methodIlGenerator.Emit(OpCodes.Ldstr, requestAttribute.Path);

            // Ctor the RequestInfo
            // Stack: [this.requester, requestInfo]
            methodIlGenerator.Emit(OpCodes.Newobj, requestInfoCtor);
        }

        private void AddParameters(ILGenerator methodIlGenerator, ParameterGrouping parameterGrouping, string methodName)
        {
            // If there's a body, add it
            if (parameterGrouping.Body != null)
            {
                var body = parameterGrouping.Body.Value;
                this.AddBody(methodIlGenerator, body.Attribute.SerializationMethod, body.Parameter.ParameterType, (short)body.Index);
            }

            // If there's a query map, add it
            if (parameterGrouping.QueryMap != null)
            {
                var queryMap = parameterGrouping.QueryMap.Value;
                if (!DictionaryIterator.CanIterate(queryMap.Parameter.ParameterType))
                    throw new ImplementationCreationException(String.Format("[QueryMap] parameter is not of type IDictionary or IDictionary<TKey, TValue> (or one of their descendents). Method: {0}", methodName));
                this.AddQueryMap(methodIlGenerator, queryMap.Parameter.ParameterType, (short)queryMap.Index);
            }

            foreach (var queryParameter in parameterGrouping.QueryParameters)
            {
                var method = addQueryParameterMethod.MakeGenericMethod(queryParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, queryParameter.Attribute.Name ?? queryParameter.Parameter.Name, (short)queryParameter.Index, method);
            }

            foreach (var plainParameter in parameterGrouping.PlainParameters)
            {
                var method = addQueryParameterMethod.MakeGenericMethod(plainParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, plainParameter.Parameter.Name, (short)plainParameter.Index, method);
            }

            foreach (var pathParameter in parameterGrouping.PathParameters)
            {
                var method = addPathParameterMethod.MakeGenericMethod(pathParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, pathParameter.Attribute.Name ?? pathParameter.Parameter.Name, (short)pathParameter.Index, method);
            }

            foreach (var headerParameter in parameterGrouping.HeaderParameters)
            {
                if (headerParameter.Attribute.Value != null)
                    throw new ImplementationCreationException(String.Format("[Header(\"{0}\", \"{1}\")] for method {2} must have the form [Header(\"Name\")], not [Header(\"Name\", \"Value\")]", headerParameter.Attribute.Name, headerParameter.Attribute.Value, methodName));
                if (headerParameter.Attribute.Name.Contains(':'))
                    throw new ImplementationCreationException(String.Format("[Header(\"{0}\")] on method {1} must not have a colon in its name", headerParameter.Attribute.Name, methodName));
                var typedMethod = addHeaderParameterMethod.MakeGenericMethod(headerParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, headerParameter.Attribute.Name, (short)headerParameter.Index, typedMethod);
            }
        }

        private void AddRequestMethodInvocation(ILGenerator methodIlGenerator, MethodInfo methodInfo)
        { 
            // Call the appropriate RequestVoidAsync/RequestAsync method, depending on whether or not we have a return type
            if (methodInfo.ReturnType == typeof(Task))
            {
                // Stack: [Task]
                methodIlGenerator.Emit(OpCodes.Callvirt, requestVoidAsyncMethod);
            }
            else if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var typeOfT = methodInfo.ReturnType.GetGenericArguments()[0];
                // Now, is it a Task<HttpResponseMessage>, a Task<string>, a Task<Response<T>> or a Task<T>?
                if (typeOfT == typeof(HttpResponseMessage))
                {
                    // Stack: [Task<HttpResponseMessage>]
                    methodIlGenerator.Emit(OpCodes.Callvirt, requestWithResponseMessageAsyncMethod);
                }
                else if (typeOfT == typeof(string))
                {
                    // Stack: [Task<string>]
                    methodIlGenerator.Emit(OpCodes.Callvirt, requestRawAsyncMethod);
                }
                else if (typeOfT.IsGenericType && typeOfT.GetGenericTypeDefinition() == typeof(Response<>))
                {
                    // Stack: [Task<Response<T>>]
                    var typedRequestWithResponseAsyncMethod = requestWithResponseAsyncMethod.MakeGenericMethod(typeOfT.GetGenericArguments()[0]);
                    methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestWithResponseAsyncMethod);
                }
                else
                {
                    // Stack: [Task<T>]
                    var typedRequestAsyncMethod = requestAsyncMethod.MakeGenericMethod(typeOfT);
                    methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestAsyncMethod);
                }
            }
            else
            {
                throw new ImplementationCreationException(String.Format("Method {0} has a return type that is not Task<T> or Task", methodInfo.Name));
            }
        }

        private void AddBody(ILGenerator methodIlGenerator, BodySerializationMethod serializationMethod, Type parameterType, short parameterIndex)
        {
            // Equivalent C#:
            // requestInfo.SetBodyParameterInfo(serializationMethod, value)
            var typedMethod = setBodyParameterInfoMethod.MakeGenericMethod(parameterType);

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, serializationMethod]
            methodIlGenerator.Emit(OpCodes.Ldc_I4, (int)serializationMethod);
            // Stack: [..., requestInfo, requestInfo, serializationMethod, parameter]
            methodIlGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, typedMethod);
        }

        private void AddQueryMap(ILGenerator methodILGenerator, Type parameterType, short parameterIndex)
        {
            // Equivalent C#:
            // requestInfo.QueryMap = value
            // They might possible potentially provide a struct here (although it's unlikely), so we need to box

            methodILGenerator.Emit(OpCodes.Dup);
            methodILGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            if (parameterType.IsValueType)
                methodILGenerator.Emit(OpCodes.Box);
            methodILGenerator.Emit(OpCodes.Callvirt, queryMapSetter);
        }

        private void AddMethodHeader(ILGenerator methodIlGenerator, HeaderAttribute header)
        {
            // Equivalent C#:
            // requestInfo.AddMethodHeader("name", "value");

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, "name"]
            methodIlGenerator.Emit(OpCodes.Ldstr, header.Name);
            // Stack: [..., requestInfo, requestInfo, "name", "value"]
            methodIlGenerator.Emit(OpCodes.Ldstr, header.Value);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, addMethodHeaderMethod);
        }

        private void AddParam(ILGenerator methodIlGenerator, string name, short parameterIndex, MethodInfo methodToCall)
        {
            // Equivalent C#:
            // requestInfo.methodToCall("name", value);
            // where 'value' is the parameter at index parameterIndex

            // Duplicate the requestInfo. This is because calling AddQueryParameter on it will pop it
            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Load the name onto the stack
            // Stack: [..., requestInfo, requestInfo, name]
            methodIlGenerator.Emit(OpCodes.Ldstr, name);
            // Load the param onto the stack
            // Stack: [..., requestInfo, requestInfo, name, value]
            methodIlGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            // Call AddPathParameter
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, methodToCall);
        }

        private void ValidatePathParams(string path, IEnumerable<string> pathParams, string methodName)
        {
            // Check that there are no duplicate param names in the attributes
            var duplicateKey = pathParams.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1);
            if (duplicateKey != null)
                throw new ImplementationCreationException(String.Format("Found more than one path parameter for key {0}. Method: {1}", duplicateKey, methodName));

            // Check that each placeholder has a matching attribute, and vice versa
            var pathPartsSet = new HashSet<string>(pathParamMatch.Matches(path).Cast<Match>().Select(x => x.Groups[1].Value));
            pathPartsSet.SymmetricExceptWith(pathParams);
            var firstInvalid = pathPartsSet.FirstOrDefault();
            if (firstInvalid != null)
                throw new ImplementationCreationException(String.Format("Unable to find both a placeholder {{{0}}} and a [PathParam(\"{0}\")] for parameter {0}. Method: {1}", firstInvalid, methodName));
        }
    }
}
