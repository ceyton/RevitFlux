using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using RevitFlux.Server.Connection;
using RevitFlux.Server.Tools;
using Xunit;

namespace RevitFlux.Tests.Server
{
    public class ServerToolContractTests
    {
        private static MethodInfo GetMethod(Type declaringType, string methodName)
        {
            return Assert.Single(declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.Name == methodName);
        }

        private static ParameterInfo GetParameter(MethodInfo method, string parameterName)
        {
            return Assert.Single(method.GetParameters(), p => p.Name == parameterName);
        }

        private static void AssertDescription(MethodInfo method, string expected)
        {
            var attribute = method.GetCustomAttribute<DescriptionAttribute>();
            Assert.NotNull(attribute);
            Assert.Equal(expected, attribute!.Description);
        }

        [Fact]
        public void GetCurrentViewElements_ExposesExplicitCategoryLists()
        {
            var method = GetMethod(typeof(ViewTools), nameof(ViewTools.GetCurrentViewElements));
            var parameters = method.GetParameters();

            Assert.Collection(
                parameters.Select(p => p.Name),
                name => Assert.Equal("revit", name),
                name => Assert.Equal("limit", name),
                name => Assert.Equal("modelCategoryList", name),
                name => Assert.Equal("annotationCategoryList", name),
                name => Assert.Equal("categoryFilter", name),
                name => Assert.Equal("fields", name),
                name => Assert.Equal("ct", name));

            Assert.Equal(typeof(RevitConnectionManager), GetParameter(method, "revit").ParameterType);
            Assert.Equal(typeof(int?), GetParameter(method, "limit").ParameterType);
            Assert.Equal(typeof(string[]), GetParameter(method, "modelCategoryList").ParameterType);
            Assert.Equal(typeof(string[]), GetParameter(method, "annotationCategoryList").ParameterType);
            Assert.Equal(typeof(string), GetParameter(method, "categoryFilter").ParameterType);
            Assert.Equal(typeof(string[]), GetParameter(method, "fields").ParameterType);
            Assert.Equal(typeof(CancellationToken), GetParameter(method, "ct").ParameterType);

            Assert.True(GetParameter(method, "modelCategoryList").HasDefaultValue);
            Assert.Null(GetParameter(method, "modelCategoryList").DefaultValue);
            Assert.True(GetParameter(method, "annotationCategoryList").HasDefaultValue);
            Assert.Null(GetParameter(method, "annotationCategoryList").DefaultValue);
            Assert.True(GetParameter(method, "categoryFilter").HasDefaultValue);
            Assert.Null(GetParameter(method, "categoryFilter").DefaultValue);

            AssertDescription(method, "List elements visible in the currently active view.");
        }

        [Fact]
        public void GetScheduleData_ExposesMaxRows()
        {
            var method = GetMethod(typeof(ViewTools), nameof(ViewTools.GetScheduleData));
            var parameters = method.GetParameters();

            Assert.Collection(
                parameters.Select(p => p.Name),
                name => Assert.Equal("revit", name),
                name => Assert.Equal("scheduleId", name),
                name => Assert.Equal("maxRows", name),
                name => Assert.Equal("ct", name));

            Assert.Equal(typeof(long), GetParameter(method, "scheduleId").ParameterType);
            Assert.Equal(typeof(int?), GetParameter(method, "maxRows").ParameterType);
            Assert.True(GetParameter(method, "maxRows").HasDefaultValue);
            Assert.Null(GetParameter(method, "maxRows").DefaultValue);

            AssertDescription(method, "Export schedule data as JSON from an existing schedule view.");
        }

        [Fact]
        public void HighCostWrappers_ExposeCompactFlags()
        {
            var familyTypes = GetMethod(typeof(ProjectTools), nameof(ProjectTools.GetAvailableFamilyTypes));
            var schedulableFields = GetMethod(typeof(ProjectTools), nameof(ProjectTools.ListSchedulableFields));
            var roomOpenings = GetMethod(typeof(ElementTools), nameof(ElementTools.GetRoomOpenings));

            Assert.Contains("compact", familyTypes.GetParameters().Select(p => p.Name));
            Assert.Contains("compact", schedulableFields.GetParameters().Select(p => p.Name));
            Assert.Contains("summaryOnly", schedulableFields.GetParameters().Select(p => p.Name));
            Assert.Contains("compact", roomOpenings.GetParameters().Select(p => p.Name));
            Assert.Contains("summaryOnly", roomOpenings.GetParameters().Select(p => p.Name));
        }

        [Fact]
        public void GetElementParameters_ExposesCompactFlag()
        {
            var method = GetMethod(typeof(ElementTools), nameof(ElementTools.GetElementParameters));
            var parameters = method.GetParameters();

            Assert.Contains("compact", parameters.Select(p => p.Name));
            Assert.Equal(typeof(bool), GetParameter(method, "compact").ParameterType);
            Assert.True(GetParameter(method, "compact").HasDefaultValue);
            Assert.Equal(true, GetParameter(method, "compact").DefaultValue);
        }

        [Fact]
        public void GetElementSolidGeometry_ExposesElementIdAndMaxSolids()
        {
            var method = GetMethod(typeof(ElementTools), nameof(ElementTools.GetElementSolidGeometry));
            var parameters = method.GetParameters();

            Assert.Collection(
                parameters.Select(p => p.Name),
                name => Assert.Equal("revit", name),
                name => Assert.Equal("elementId", name),
                name => Assert.Equal("maxSolids", name),
                name => Assert.Equal("ct", name));

            Assert.Equal(typeof(RevitConnectionManager), GetParameter(method, "revit").ParameterType);
            Assert.Equal(typeof(long), GetParameter(method, "elementId").ParameterType);
            Assert.Equal(typeof(int), GetParameter(method, "maxSolids").ParameterType);
            Assert.Equal(typeof(CancellationToken), GetParameter(method, "ct").ParameterType);

            Assert.True(GetParameter(method, "maxSolids").HasDefaultValue);
            Assert.Equal(20, GetParameter(method, "maxSolids").DefaultValue);

            Assert.NotNull(method.GetCustomAttribute<DescriptionAttribute>());
        }

        [Fact]
        public void AuditFamilies_ExposesCompactFlag()
        {
            var method = GetMethod(typeof(ProjectTools), nameof(ProjectTools.AuditFamilies));
            var parameters = method.GetParameters();

            Assert.Contains("compact", parameters.Select(p => p.Name));
            Assert.Equal(typeof(bool), GetParameter(method, "compact").ParameterType);
            Assert.True(GetParameter(method, "compact").HasDefaultValue);
            Assert.Equal(true, GetParameter(method, "compact").DefaultValue);
        }

        [Fact]
        public void GetAvailableFamilyTypes_ExposesToolNativeParameters()
        {
            var method = GetMethod(typeof(ProjectTools), nameof(ProjectTools.GetAvailableFamilyTypes));
            var parameters = method.GetParameters();

            Assert.Collection(
                parameters.Select(p => p.Name),
                name => Assert.Equal("revit", name),
                name => Assert.Equal("categoryList", name),
                name => Assert.Equal("familyNameFilter", name),
                name => Assert.Equal("limit", name),
                name => Assert.Equal("compact", name),
                name => Assert.Equal("ct", name));

            Assert.Equal(typeof(string[]), GetParameter(method, "categoryList").ParameterType);
            Assert.Equal(typeof(string), GetParameter(method, "familyNameFilter").ParameterType);
            Assert.Equal(typeof(int?), GetParameter(method, "limit").ParameterType);

            Assert.True(GetParameter(method, "categoryList").HasDefaultValue);
            Assert.Null(GetParameter(method, "categoryList").DefaultValue);
            Assert.True(GetParameter(method, "familyNameFilter").HasDefaultValue);
            Assert.Null(GetParameter(method, "familyNameFilter").DefaultValue);
            Assert.True(GetParameter(method, "limit").HasDefaultValue);
            Assert.Null(GetParameter(method, "limit").DefaultValue);
        }

        [Fact]
        public void ModifySchedule_DescribesPluginNativeSortingActions()
        {
            var method = GetMethod(typeof(ProjectTools), nameof(ProjectTools.ModifySchedule));
            var action = GetParameter(method, "action");

            var description = action.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.NotNull(description);
            Assert.Contains("set_sorting", description);
            Assert.Contains("clear_sorting", description);
            Assert.Contains("set_filter", description);
            Assert.Contains("clear_filter", description);
            Assert.DoesNotContain("set_sort |", description);

            AssertDescription(method,
                "Modify schedule fields, sorting, filters, or rename the schedule. Supported actions: add_field, remove_field, set_sorting, clear_sorting, set_filter, clear_filter, rename.");
        }
    }
}
