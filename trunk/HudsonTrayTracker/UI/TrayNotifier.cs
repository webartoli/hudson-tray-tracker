using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Text;
using System.Windows.Forms;
using Hudson.TrayTracker.BusinessComponents;
using Dotnet.Commons.Logging;
using System.Reflection;
using Hudson.TrayTracker.Entities;
using Hudson.TrayTracker.Properties;
using Hudson.TrayTracker.Utils.Logging;
using Iesi.Collections.Generic;
using DevExpress.XtraEditors;

namespace Hudson.TrayTracker.UI
{
    public partial class TrayNotifier : Component
    {
        static readonly ILog logger = LogFactory.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        static TrayNotifier instance;
        public static TrayNotifier Instance
        {
            get
            {
                if (instance == null)
                    instance = new TrayNotifier();
                return instance;
            }
        }

        ConfigurationService configurationService;
        HudsonService hudsonService;
        ProjectsUpdateService updateService;
        BuildStatus lastBuildStatus;
        IDictionary<Project, AllBuildDetails> lastProjectsBuildDetails = new Dictionary<Project, AllBuildDetails>();
        IDictionary<Project, BuildStatus> acknowledgedStatusByProject = new Dictionary<Project, BuildStatus>();
        IDictionary<BuildStatus, Icon> icons;

        public ConfigurationService ConfigurationService
        {
            get { return configurationService; }
            set { configurationService = value; }
        }

        public HudsonService HudsonService
        {
            get { return hudsonService; }
            set { hudsonService = value; }
        }

        public ProjectsUpdateService UpdateService
        {
            get { return updateService; }
            set { updateService = value; }
        }

        public TrayNotifier()
        {
            InitializeComponent();
            LoadIcons();
        }

        public void Initialize()
        {
            configurationService.ConfigurationUpdated += configurationService_ConfigurationUpdated;
            updateService.ProjectsUpdated += updateService_ProjectsUpdated;

            Disposed += delegate
            {
                configurationService.ConfigurationUpdated -= configurationService_ConfigurationUpdated;
                updateService.ProjectsUpdated -= updateService_ProjectsUpdated;
            };
        }

        void configurationService_ConfigurationUpdated()
        {
            UpdateNotifier();
        }

#if false
        private delegate void ProjectsUpdatedDelegate();
        private void updateService_ProjectsUpdated()
        {
            Delegate del = new ProjectsUpdatedDelegate(OnProjectsUpdated);
            MainForm.Instance.BeginInvoke(del);
        }
        private void OnProjectsUpdated()
        {
            UpdateGlobalStatus();
        }
#else
        private void updateService_ProjectsUpdated()
        {
            UpdateNotifier();
        }
#endif

        // FIXME: the framework doesn't fire correctly MouseClick and MouseDoubleClick,
        // so this is deactivated
        private void notifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            try
            {
                // order the projects by build status
                IDictionary<BuildStatus, SortedSet<Project>> projectsByStatus
                    = new Dictionary<BuildStatus, SortedSet<Project>>();
                foreach (KeyValuePair<Project, AllBuildDetails> pair in lastProjectsBuildDetails)
                {
                    BuildStatus status = BuildStatus.Unknown;
                    if (pair.Value != null)
                        status = BuildStatusUtils.DegradeStatus(pair.Value.Status);
                    SortedSet<Project> projects = new SortedSet<Project>();
                    if (projectsByStatus.TryGetValue(status, out projects) == false)
                    {
                        projects = new SortedSet<Project>();
                        projectsByStatus.Add(status, projects);
                    }
                    projects.Add(pair.Key);
                }

                StringBuilder text = new StringBuilder();
                string prefix = null;
                foreach (KeyValuePair<BuildStatus, SortedSet<Project>> pair in projectsByStatus)
                {
                    // don't display successful projects unless this is the only status
                    if (pair.Key == BuildStatus.Successful || projectsByStatus.Count == 1)
                        continue;

                    if (prefix != null)
                        text.Append(prefix);
                    string statusText = HudsonTrayTrackerResources.ResourceManager
                        .GetString("BuildStatus_" + pair.Key.ToString());
                    text.Append(statusText);
                    foreach (Project project in pair.Value)
                        text.Append("\n  - ").Append(project.Name);
                    prefix = "\n";
                }

                string textToDisplay = text.ToString();
                if (string.IsNullOrEmpty(textToDisplay))
                    textToDisplay = HudsonTrayTrackerResources.DisplayBuildStatus_NoProjects;
                notifyIcon.ShowBalloonTip(10000, HudsonTrayTrackerResources.DisplayBuildStatus_Caption,
                    textToDisplay, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                LoggingHelper.LogError(logger, ex);
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            MainForm.ShowOrFocus();
        }

        private void openMenuItem_Click(object sender, EventArgs e)
        {
            MainForm.ShowOrFocus();
        }

        private void refreshMenuItem_Click(object sender, EventArgs e)
        {
            updateService.UpdateProjects();
        }

        private void settingsMenuItem_Click(object sender, EventArgs e)
        {
            MainForm.Instance.Show();
            SettingsForm.ShowDialogOrFocus();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MainForm.Instance.Exit();
        }

        private void aboutMenuItem_Click(object sender, EventArgs e)
        {
            MainForm.Instance.Show();
            AboutForm.ShowDialogOrFocus();
        }

        public void UpdateNotifier()
        {
            try
            {
                DoUpdateNotifier();
            }
            catch (Exception ex)
            {
                LoggingHelper.LogError(logger, ex);
                MessageBox.Show(ex.ToString(), "Program exception handler");
                throw;
            }
        }

        private void DoUpdateNotifier()
        {
            BuildStatus? worstBuildStatus = null;
            bool buildInProgress = false;
            ISet<Project> errorProjects = new HashedSet<Project>();
            ISet<Project> regressingProjects = new HashedSet<Project>();

            foreach (Server server in configurationService.Servers)
            {
                foreach (Project project in server.Projects)
                {
                    BuildStatus status = GetProjectStatus(project);
                    if (worstBuildStatus == null || status > worstBuildStatus)
                        worstBuildStatus = status;
                    if (status >= BuildStatus.Failed)
                        errorProjects.Add(project);
                    if (BuildStatusUtils.IsBuildInProgress(status))
                        buildInProgress = true;
                    if (IsRegressing(project))
                        regressingProjects.Add(project);
                    lastProjectsBuildDetails[project] = project.AllBuildDetails;
                }
            }

            if (worstBuildStatus == null)
                worstBuildStatus = BuildStatus.Unknown;

#if false // tests
            lastBuildStatus++;
            if (lastBuildStatus > BuildStatus.Failed_BuildInProgress)
                lastBuildStatus = 0;
            worstBuildStatus = lastBuildStatus;
            Console.WriteLine("tray:"+lastBuildStatus);
#endif

            BuildStatus buildStatus = worstBuildStatus.Value;
            // if a build is in progress and the worst status is not a build-in-progress status,
            // switch to the nearest build-in-progress status
            if (buildInProgress && BuildStatusUtils.IsBuildInProgress(buildStatus) == false)
                buildStatus += 1;

            UpdateIcon(buildStatus);
            UpdateBalloonTip(errorProjects, regressingProjects);

            lastBuildStatus = buildStatus;
        }

        private BuildStatus GetProjectStatus(Project project)
        {
            BuildStatus status = project.Status;
            BuildStatus? acknowledgedStatus = GetAcknowledgedStatus(project);
            if (acknowledgedStatus != null && status == acknowledgedStatus)
                return BuildStatus.Successful;
            return status;
        }

        private bool IsRegressing(Project project)
        {
            AllBuildDetails lastBuildDetails;
            if (lastProjectsBuildDetails.TryGetValue(project, out lastBuildDetails) == false
                || lastBuildDetails == null)
                return false;
            AllBuildDetails newBuildDetails = project.AllBuildDetails;
            if (newBuildDetails == null)
                return false;
            bool res = BuildStatusUtils.IsWorse(newBuildDetails.Status, lastBuildDetails.Status);
            return res;
        }

        private void UpdateBalloonTip(ICollection<Project> errorProjects, ICollection<Project> regressingProjects)
        {
            if (lastBuildStatus < BuildStatus.Failed && errorProjects.Count > 0)
            {
                StringBuilder errorProjectsText = new StringBuilder();
                string prefix = null;
                foreach (Project project in errorProjects)
                {
                    if (prefix != null)
                        errorProjectsText.Append(prefix);
                    errorProjectsText.Append(project.Name);
                    prefix = "\n";
                }

                notifyIcon.ShowBalloonTip(10000, HudsonTrayTrackerResources.BuildFailed_Caption,
                    errorProjectsText.ToString(), ToolTipIcon.Error);
            }
            else if (regressingProjects.Count > 0)
            {
                StringBuilder regressingProjectsText = new StringBuilder();
                string prefix = null;
                foreach (Project project in regressingProjects)
                {
                    if (prefix != null)
                        regressingProjectsText.Append(prefix);
                    regressingProjectsText.Append(project.Name);
                    prefix = "\n";
                }

                notifyIcon.ShowBalloonTip(10000, HudsonTrayTrackerResources.BuildRegressions_Caption,
                    regressingProjectsText.ToString(), ToolTipIcon.Warning);
            }
        }

        private void UpdateIcon(BuildStatus buildStatus)
        {
            Icon icon = icons[buildStatus];
            notifyIcon.Icon = icon;
        }

        private void LoadIcons()
        {
            icons = new Dictionary<BuildStatus, Icon>();

            foreach (BuildStatus buildStatus in Enum.GetValues(typeof(BuildStatus)))
            {
                try
                {
                    string resourceName = string.Format("Hudson.TrayTracker.Resources.TrayIcons.{0}.gif",
                        buildStatus.ToString());
                    Bitmap bitmap = DevExpress.Utils.Controls.ImageHelper.CreateBitmapFromResources(
                        resourceName, GetType().Assembly);
                    IntPtr hicon = bitmap.GetHicon();
                    Icon icon = Icon.FromHandle(hicon);
                    icons.Add(buildStatus, icon);
                }
                catch (Exception ex)
                {
                    XtraMessageBox.Show(HudsonTrayTrackerResources.FailedLoadingIcons_Text,
                        HudsonTrayTrackerResources.FailedLoadingIcons_Caption,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    LoggingHelper.LogError(logger, ex);
                    throw new Exception("Failed loading icon: " + buildStatus, ex);
                }
            }
        }

        private void notifyIcon_MouseUp(object sender, MouseEventArgs e)
        {
            Console.WriteLine(e.Clicks);
        }

        public void AcknowledgeStatus(Project project, BuildStatus currentStatus)
        {
            lock (acknowledgedStatusByProject)
            {
                acknowledgedStatusByProject[project] = currentStatus;
            }
            UpdateNotifier();
        }

        public void ClearAcknowledgedStatus(Project project)
        {
            lock (acknowledgedStatusByProject)
            {
                acknowledgedStatusByProject.Remove(project);
            }
            UpdateNotifier();
        }

        private BuildStatus? GetAcknowledgedStatus(Project project)
        {
            BuildStatus status;
            lock (acknowledgedStatusByProject)
            {
                if (acknowledgedStatusByProject.TryGetValue(project, out status) == false)
                    return null;
            }
            return status;
        }

        public bool IsAcknowledged(Project project)
        {
            lock (acknowledgedStatusByProject)
            {
                return acknowledgedStatusByProject.ContainsKey(project);
            }
        }
    }
}