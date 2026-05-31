#nullable enable
using System;
using System.Collections.Generic;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XAFProfiler.Blazor.Server.BusinessObjects;
using XAFProfiler.Blazor.Server.Services;
using InformationType = DevExpress.ExpressApp.InformationType;

namespace XAFProfiler.Blazor.Server.Controllers
{
    /// <summary>
    /// Provides "Clear Profiles" and "Delete Selected" maintenance actions on the
    /// ProfileSummary ListView. Both delegate SQL-level deletion to
    /// <see cref="ProfileStore"/>, then refresh the view.
    ///
    /// <para>
    /// Targeting: <see cref="ViewController"/> with explicit TargetObjectType /
    /// TargetViewType (ListView) so actions appear only on the ProfileSummary list.
    /// </para>
    /// </summary>
    public sealed class ProfileMaintenanceController : ViewController
    {
        private readonly SimpleAction _clearProfilesAction;
        private readonly SimpleAction _deleteSelectedAction;
        private ILogger<ProfileMaintenanceController>? _logger;

        public ProfileMaintenanceController()
        {
            TargetObjectType = typeof(ProfileSummary);
            TargetViewType = ViewType.ListView;

            // ── Clear Profiles ────────────────────────────────────────────────────────────
            _clearProfilesAction = new SimpleAction(
                this,
                "ClearProfiles",
                "Tools")
            {
                Caption = "Clear Profiles",
                ConfirmationMessage = "Delete ALL stored profiles? This cannot be undone.",
                ImageName = "Action_Clear",
                ToolTip = "Permanently remove all captured profiling data from storage.",
            };
            _clearProfilesAction.Execute += ClearProfilesAction_Execute;

            // ── Delete Selected ───────────────────────────────────────────────────────────
            _deleteSelectedAction = new SimpleAction(
                this,
                "DeleteSelectedProfiles",
                "Tools")
            {
                Caption = "Delete Selected",
                SelectionDependencyType = SelectionDependencyType.RequireMultipleObjects,
                ImageName = "Action_Delete",
                ToolTip = "Permanently remove the selected profiling rows from storage.",
            };
            _deleteSelectedAction.Execute += DeleteSelectedAction_Execute;
        }

        protected override void OnActivated()
        {
            base.OnActivated();
            _logger = Application.ServiceProvider?.GetService<ILogger<ProfileMaintenanceController>>();
        }

        protected override void OnDeactivated()
        {
            _logger = null;
            base.OnDeactivated();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clearProfilesAction.Execute -= ClearProfilesAction_Execute;
                _deleteSelectedAction.Execute -= DeleteSelectedAction_Execute;
            }
            base.Dispose(disposing);
        }

        // ── Action handlers ───────────────────────────────────────────────────────────────

        private void ClearProfilesAction_Execute(object? sender, SimpleActionExecuteEventArgs e)
        {
            var connStr = ResolveConnectionString();
            if (connStr == null)
            {
                ShowMessage("Cannot clear profiles: connection string not configured.", InformationType.Warning);
                return;
            }

            try
            {
                _logger?.LogInformation("ProfileMaintenanceController: clearing all profiles.");
                ProfileStore.ClearAll(connStr, _logger);
                _logger?.LogInformation("ProfileMaintenanceController: all profiles cleared.");
                RefreshView();
                ShowMessage("All profiles cleared.", InformationType.Success);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ProfileMaintenanceController: ClearAll failed.");
                ShowMessage($"Clear failed: {ex.Message}", InformationType.Error);
            }
        }

        private void DeleteSelectedAction_Execute(object? sender, SimpleActionExecuteEventArgs e)
        {
            var connStr = ResolveConnectionString();
            if (connStr == null)
            {
                ShowMessage("Cannot delete profiles: connection string not configured.", InformationType.Warning);
                return;
            }

            // Collect selected ProfileSummary ids from the current selection.
            var selectedIds = new List<Guid>();
            foreach (var obj in e.SelectedObjects)
            {
                if (obj is ProfileSummary summary)
                {
                    selectedIds.Add(summary.Id);
                }
            }

            if (selectedIds.Count == 0)
            {
                ShowMessage("No profiles selected.", InformationType.Warning);
                return;
            }

            try
            {
                _logger?.LogInformation(
                    "ProfileMaintenanceController: deleting {Count} selected profile(s).", selectedIds.Count);
                ProfileStore.DeleteByIds(connStr, selectedIds, _logger);
                _logger?.LogInformation(
                    "ProfileMaintenanceController: {Count} profile(s) deleted.", selectedIds.Count);
                RefreshView();
                ShowMessage($"{selectedIds.Count} profile(s) deleted.", InformationType.Success);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ProfileMaintenanceController: DeleteByIds failed.");
                ShowMessage($"Delete failed: {ex.Message}", InformationType.Error);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────

        private string? ResolveConnectionString()
        {
            var config = Application.ServiceProvider?.GetService<IConfiguration>();
            return config?.GetConnectionString("ConnectionString");
        }

        private void RefreshView()
        {
            try
            {
                View?.ObjectSpace?.Refresh();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ProfileMaintenanceController: view refresh failed.");
            }
        }

        private void ShowMessage(string message, InformationType type = InformationType.Success)
        {
            try
            {
                Application?.ShowViewStrategy?.ShowMessage(message, type, 3000);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ProfileMaintenanceController: ShowMessage failed.");
            }
        }
    }
}
