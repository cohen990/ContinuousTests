using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using AutoTest.VS.CommandHandling;
using Extensibility;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;
using AutoTest.Client.Logging;
using AutoTest.VS.ClientHandlers;
using AutoTest.VS.Listeners;
using AutoTest.VS.Util;
using AutoTest.Core.ReflectionProviders;
using AutoTest.VS.Util.Builds;
using AutoTest.VS.Util.Menues;
using AutoTest.VS.Util.CommandHandling;
using AutoTest.VS;
using AutoTest.VS.Util.Debugger;
using AutoTest.Messages;
using AutoTest.Client.HTTP;

namespace ContinuousTests.VS
{
   /// <summary>The object for implementing an Add-in.</summary>
   /// <seealso class='IDTExtensibility2' />
   public partial class Connect : IDTExtensibility2, IDTCommandTarget
   {
       private object [] contextGUIDS = new object[] { };
       public DTE2 _applicationObject;
       private AddIn _addInInstance;
       public static AutoTestVSRunInformation _control;
       public static ContinuousTests_ListOfRanTests LastRanTestsControl = null;
       public static Window LastRanTestsWindow = null;
       private static bool _initialized = false;
       private bool _initializedMenu = false;
       private readonly CommandDispatcher _dispatchers = new CommandDispatcher();
       private VSBuildRunner _buildRunner;

       /// <summary>Implements the constructor for the Add-in object.Place your initialization code within this method.</summary>
       public Connect()
       {
           try
           {
               _syncContext = SynchronizationContext.Current;
               AppDomain.CurrentDomain.UnhandledException += LogExceptions;
               Application.ThreadException += Application_ThreadException;
               Solution = "";
               AutoTest.TestRunners.Shared.AssemblyAnalysis.Reflect.ScratchThat_InsteadUseThisAwesome(
                   (assembly) => { return new CecilReflectionProvider(assembly); });
               StartupHandler = new StartupHandler();
           }
           catch(Exception ex)
           {
               Logger.Write("Connect" + ex);
           }
       }

       void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
       {
           Analytics.SendEvent("ThreadExceptionError");
           Logger.Write("UNHANDLED EXCEPTIION");
           Logger.Write(e.Exception);
       }

       private void LogExceptions(object sender, UnhandledExceptionEventArgs unhandledExceptionEventArgs)
       {
           Logger.Write("UNHANDLED EXCEPTIION");
           Logger.Write(unhandledExceptionEventArgs.ExceptionObject.ToString());
       }

       /// <summary>Implements the OnConnection method of theIDTExtensibility2 interface. Receives notification that the Add-in isbeing loaded.</summary>
       /// <param term='application'>Root object of the hostapplication.</param>
       /// <param term='connectMode'>Describes how the Add-in isbeing loaded.</param>
       /// <param term='addInInst'>Object representing this Add-in.</param>
       /// <seealso class='IDTExtensibility2' />
       public void OnConnection(object application, ext_ConnectMode connectMode, object addInInst, ref Array custom)
       {
           try
           {
               Logger.SetListener(new FileLogger());

               Logger.Write("Connecting to Visual Studio");
               _applicationObject = (DTE2) application;
               StartupHandler.SetApplication(_applicationObject);
               setupListener();
               bindWorkspaceEvents();
               SolutionStateHandler.BindEvents(_applicationObject);
               SaverOfFilesThatVSKeepsStashigInMem.BindEvents(_applicationObject);
               _addInInstance = (AddIn) addInInst;

               Logger.Write("Adding menu item");
               RegisterCommandHandlers();
               if (connectMode == ext_ConnectMode.ext_cm_UISetup || theShitIsNotThere())
               {
                   if (connectMode == ext_ConnectMode.ext_cm_UISetup)
                       Analytics.SendEvent("UI_SETUP");
                   else
                       Analytics.SendEvent("UI_SETUP_MANUAL");
                   AddMenuItems();
                   Logger.Write("Menu item added");
                   _initializedMenu = true;
               }
               AddContextMenue();
           }
           catch(Exception ex)
           {
               Logger.Write("OnConnect " + ex);
           }
       }

       private bool theShitIsNotThere()
       {
           var builder = new MenuBuilder(_applicationObject, _addInInstance);
           var menuExists = builder.MenuExists("ContinuousTests");
           if (menuExists == false)
               menuExists = builder.MenuExists("C&ontinuousTests");
           return !menuExists;
       }

       private void RegisterCommandHandlers()
       {
           _buildRunner = new VSBuildRunner(_applicationObject, () => { return !_client.IsRunning; }, (s) => Connect.SetCustomOutputpath(s), (o) => _control.IncomingMessage(o), (s) => _control.ClearBuilds(s));
           _dispatchers.RegisterHandler(new AutoTestVSConfigurationGlobal(_client));
           _dispatchers.RegisterHandler(new AutoTestVSConfigurationSolution(_client));
           _dispatchers.RegisterHandler(new AutoTestVSDetectRecursive(_client));
           _dispatchers.RegisterHandler(new AutoTestVSAbout(_client));
           _dispatchers.RegisterHandler(new AutoTestVSRunAll(_applicationObject, _client, _buildRunner));
           _dispatchers.RegisterHandler(new AutoTestVSClearTestCache(_client));
           _dispatchers.RegisterHandler(new AutoTestVSRunInformationToolbox(_applicationObject, _addInInstance));
           _dispatchers.RegisterHandler(new RunTestsUnderCursor("ContinuousTests.VS.Connect.ContinuousTests_RunUnderCursor", () => IsSolutionOpened, () => { return buildManually(); },
               run => _client.StartOnDemandRun(run), _applicationObject, _buildRunner, run => _client.SetLastRun(run)));
           _dispatchers.RegisterHandler(new AutoTestVSStart(_client));
           _dispatchers.RegisterHandler(new AutoTestVSStop(_client));
           _dispatchers.RegisterHandler(new GetAffectedCodeGraph(_client, _applicationObject));
           _dispatchers.RegisterHandler(new GetProfiledCodeGraph(_client, _applicationObject));
           _dispatchers.RegisterHandler(new AutoTestVSGetLastGraph(_client));
           _dispatchers.RegisterHandler(new GetSequenceDiagram(_client, _applicationObject));
           _dispatchers.RegisterHandler(new DebugCurrentTest("ContinuousTests.VS.Connect.ContinuousTests_DebugCurrentTest", () => IsSolutionOpened, () => { return buildManually(); },
               GetAssembly, test => Debug(_applicationObject, test), _applicationObject, _buildRunner,
               test => LastDebugRun = test));
           _dispatchers.RegisterHandler(new RerunLastManualTestRun("ContinuousTests.VS.Connect.ContinuousTests_RunLastOnDemandRun", () => { return Connect.IsSolutionOpened && _client.HasLastOnDemandRun; }, () => { return buildManually(); },
               () => _client.RunLastOnDemandRun(), _applicationObject, _buildRunner,
               () => _client.LastOnDemandRun));
           _dispatchers.RegisterHandler(new RerunLastDebugSession("ContinuousTests.VS.Connect.ContinuousTests_RunLastDebug",
               () => IsSolutionOpened && LastDebugRun != null, buildManually,
               () => Debug(_applicationObject, LastDebugRun), _applicationObject, _buildRunner));
           _dispatchers.RegisterHandler(new RunRelatedTests(_client, _applicationObject, _buildRunner));
           _dispatchers.RegisterHandler(new RealtimeToggler(_client));
           _dispatchers.RegisterHandler(new LastTestsRanHandler(_applicationObject, _addInInstance));

           _dispatchers.RegisterHandler(new RunTestsForSolution("ContinuousTests.VS.Connect.ContinuousTests_RunForSolution",
               () => IsSolutionOpened, buildManually, tests => _client.StartOnDemandRun(tests), _applicationObject, _buildRunner,
               runs => _client.SetLastRun(runs)));
           _dispatchers.RegisterHandler(new RunTestsForCodeModel("ContinuousTests.VS.Connect.ContinuousTests_RunCodeModelTests",
               () => IsSolutionOpened, buildManually, (tests) => _client.StartOnDemandRun(tests), _applicationObject, _buildRunner,
               projects => _client.GetProjectBuildList(projects.Select(x => x.Project)), runs => _client.SetLastRun(runs)));
           _dispatchers.RegisterHandler(new GenericCommand("ContinuousTests.VS.Connect.ContinuousTests_ReportIssue", () => true, () => Browse.To("http://moose.uservoice.com")));
       }

       public static void Debug(DTE2 app, CacheTestMessage test)
       {
           if (Connect.CannotDebug(app, test))
               return;
           new DebugHandler(app).Debug(test);
           Connect.LastDebugRun = test;
       }

       private static bool buildManually()
       {
           var buildManually = !_client.IsRunning || _client.MMConfiguration.BuildExecutables.Count() == 0;
           if (buildManually)
               DoubleBuildOnDemandHandler.PrepareForOnDemandRun();
           return buildManually;
       }

       private void AddContextMenue()
       {
           var builder = new MenuBuilder(_applicationObject, _addInInstance);

           builder.CreateMenuItem("Project and Solution Context Menus", "Solution", "Run Tests", "Runs all tests in solution", null, 1, "ContinuousTests_RunForSolution", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Solution Folder", "Run Tests", "Runs all tests in projects", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Project", "Run Tests", "Runs all tests in project", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Solution Folder", "Run Tests", "Runs all tests in solution folders", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Project", "Run Tests", "Runs all tests in projects", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Item", "Run Tests", "Runs all tests in project item", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Folder", "Run Tests", "Runs all tests in project item", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Item", "Run Tests", "Runs all tests in project items", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Project and Solution Context Menus", "Cross Project Multi Project/Folder", "Run Tests", "Runs all tests in projects and solution folders", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Class View Context Menus", "Class View Project", "Run Tests", "Runs all tests in project", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Class View Context Menus", "Class View Item", "Run Tests", "Runs all tests in member", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Class View Context Menus", "Class View Folder", "Run Tests", "Runs all tests in folder", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);
           builder.CreateMenuItem("Class View Context Menus", "Class View Multi-select", "Run Tests", "Runs all tests in members", null, 1, "ContinuousTests_RunCodeModelTests", false, 1);

           CommandBarControl ctl = builder.CreateMenuContainer("Editor Context Menus", "Code Window", "ContinuousTests", "ContinuousTests features", null, 1);
           builder.CreateSubMenuItem(ctl, "Run Test(s)", "Runs all tests in current scope", "Global::ctrl+shift+y,u", 1, "ContinuousTests_RunUnderCursor");
           builder.CreateSubMenuItem(ctl, "Run Related Tests", "Runs all tests related to the code under cursor", "Global::ctrl+shift+y,r", 2, "ContinuousTests_RunRelatedTests");
           builder.CreateSubMenuItem(ctl, "Rerun Last Manual Test Run", "Reruns last manual test run", "Global::ctrl+shift+y,e", 3, "ContinuousTests_RunLastOnDemandRun");
           builder.CreateSubMenuItem(ctl, "Debug Test", "Debug test", "Global::ctrl+shift+y,d", 4, "ContinuousTests_DebugCurrentTest", true, 0);
           builder.CreateSubMenuItem(ctl, "Rerun Last Debug Session", "Reruns last debug session", "Global::ctrl+shift+y,w", 5, "ContinuousTests_RunLastDebug");
           builder.CreateSubMenuItem(ctl, "Get Affected Graph", "Gets the affected graph for this item", "Global::ctrl+shift+y,g", 6, "ContinuousTests_GetAffectedCodeGraph", true, 0);
           builder.CreateSubMenuItem(ctl, "Get Profiled Graph", "Gets the profiled graph for this item", "Global::ctrl+shift+y,p", 7, "ContinuousTests_GetProfiledCodeGraph", true, 0);
           builder.CreateSubMenuItem(ctl, "Get Sequence Diagram", "Gets the sequence diagram of what this test does at runtime", "Global::ctrl+shift+y,s", 8, "ContinuousTests_GetSequenceDiagram", true, 0);
       }

       private void AddMenuItems()
       {
           var builder = new MenuBuilder(_applicationObject, _addInInstance);
           AddMenuBar("C&ontinuousTests");

           // Current menu items
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Run Output Window", "Main feedback window", "Global::ctrl+shift+j", 1, "ContinuousTests_FeedbackWindow", false, 1);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Last Ran Graph", "Last run graph", "Global::ctrl+shift+y,l", 2, "ContinuousTests_LastGraph", false, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Last Ran Tests Window", "Contains tests from last test run", "Global::ctrl+shift+y,p", 3, "ContinuousTests_LastRanTestsWindow", false, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Resume Engine", "Resumes the AutoTest.Net engine", null, 4, "ContinuousTests_ResumeEngine", true, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Pause Engine", "Pauses the AutoTest.Net engine", null, 5, "ContinuousTests_PauseEngine", false, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Build And Test All Projects", "Builds all projects and runs all tests", "Global::ctrl+shift+y,a", 6, "ContinuousTests_RunAll", true, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Toggle Realtime Mode", "Turns realtime mode on or off", "Global::ctrl+'", 7, "ContinuousTests_RealtimeToggler", false, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Clear Cached Tests And Feedback List", "Clears all cached tests and feedback lsit", "Global::ctrl+shift+y,del", 8, "ContinuousTests_ClearTestCache", false, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Detect Recursion on Next Run", "Retrieve files causing recursive build and test runs", null, 9, "ContinuousTests_DetectRecursion", false, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Configuration", "Modify solution configuration", null, 10, "ContinuousTests_SolutionConfiguration", true, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "Knowledge Base / Feedback", "Report issue for ContinuousTests", null, 11, "ContinuousTests_ReportIssue", true, 0);
           CreateMenuItem("MenuBar", "C&ontinuousTests", "About", "About ContinuousTests", null, 12, "ContinuousTests_About", false, 0);           
       }

       private void AddMenuBar(string name)
       {
           Logger.Write("Attempting to create menu item for AutoTest.NET plugin");
           //Place the command on the tools menu.
           //Find the MenuBar command bar, which is the top-level command bar holding all the main menu items:
           CommandBar menuBarCommandBar = ((CommandBars)_applicationObject.CommandBars)["MenuBar"];
           CommandBarControl toolsControl;
           var commands = (Commands2)_applicationObject.Commands;

           try
           {
               Logger.Write("Trying to retrieve menu item");
               toolsControl = menuBarCommandBar.Controls[name];
           }
           catch
           {
               Logger.Write("Menu item not found.. trying to create");
               var command =
                   (CommandBar)
                   commands.AddCommandBar(name, vsCommandBarType.vsCommandBarTypeMenu, menuBarCommandBar, 31);
               toolsControl = menuBarCommandBar.Controls[name];
           }
           toolsControl.Enabled = true;
           CommandBarPopup toolsPopup = (CommandBarPopup)toolsControl;
       }

       private void CreateMenuItem(string commandBar, string popup, string caption, string description, string bindings, int place, string commandName, bool separator, int icon)
       {
           try
           {
               Logger.Write("Creating menu item " + caption + " in " + popup);
               var commands = (Commands2)_applicationObject.Commands;
               var cBars = (CommandBars)_applicationObject.CommandBars;
               var editorCommandBar = cBars[commandBar];
               var editPopUp = (CommandBarPopup)editorCommandBar.Controls[popup];
               var command = commands.AddNamedCommand2(_addInInstance, commandName, caption, description, true, icon, ref contextGUIDS, (int)vsCommandStatus.vsCommandStatusSupported, (int)vsCommandStyle.vsCommandStylePictAndText);
               command.AddControl(editPopUp.CommandBar, place);
               var item = getCommandBarControl(editPopUp.Controls, caption);
               item.BeginGroup = separator;
               if (bindings != null)
                   command.Bindings = bindings;
           }
           catch (Exception ex)
           {
               Logger.Write("error creating menu item");
               Logger.Write(ex);
           }
       }

       private CommandBarControl getCommandBarControl(CommandBarControls controls, string name)
       {
           var enu = controls.GetEnumerator();

           while (enu.MoveNext())
           {
               var control = (CommandBarControl)enu.Current;

               if (control.accName == name)
                   return control;
           }
           return null;
       }

       private void setupListener()
       {
           if (!_initialized)
           {
               StartupHandler.AddListener(new StatusBarListener(_applicationObject, _client.MMConfiguration));
               StartupHandler.AddListener(new GraphGenerateListener(_applicationObject, _client));
               StartupHandler.AddListener(new SequenceDiagramGenerateListener(_applicationObject, _client));
               StartupHandler.AddListener(new ProfilerCorruptionListener(_applicationObject, _client));
               DoubleBuildOnDemandHandler = new AutoModeDoubleBuildOnDemandHandler(_client);
               StartupHandler.AddListener(DoubleBuildOnDemandHandler);
               _initialized = true;
           }
       }

       /// <summary>Implements the OnDisconnection method of the IDTExtensibility2 interface. Receives notification that the Add-in is being unloaded.</summary>
       /// <param term='disconnectMode'>Describes how the Add-in is being unloaded.</param>
       /// <param term='custom'>Array of parameters that are host application specific.</param>
       /// <seealso class='IDTExtensibility2' />
       public void OnDisconnection(ext_DisconnectMode disconnectMode, ref Array custom)
       {
       }

       /// <summary>Implements the OnAddInsUpdate method of the IDTExtensibility2 interface. Receives notification when the collection of Add-ins has changed.</summary>
       /// <param term='custom'>Array of parameters that are host application specific.</param>
       /// <seealso class='IDTExtensibility2' />
       public void OnAddInsUpdate(ref Array custom)
       {
       }

       /// <summary>Implements the OnStartupComplete method of the IDTExtensibility2 interface. Receives notification that the host application has completed loading.</summary>
       /// <param term='custom'>Array of parameters that are host application specific.</param>
       /// <seealso class='IDTExtensibility2' />
       public void OnStartupComplete(ref Array custom)
       {
       }

       /// <summary>Implements the OnBeginShutdown method of the IDTExtensibility2 interface. Receives notification that the host application is being unloaded.</summary>
       /// <param term='custom'>Array of parameters that are host application specific.</param>
       /// <seealso class='IDTExtensibility2' />
       public void OnBeginShutdown(ref Array custom)
       {
       }

       // Set startup so that visual things like menubar button shows up
       public void QueryStatus(string CmdName, vsCommandStatusTextWanted NeededText, ref vsCommandStatus StatusOption, ref object CommandText)
       {
           _dispatchers.QueryStatus(CmdName, NeededText, ref StatusOption, ref CommandText);
       }

       public void Exec(string CmdName, vsCommandExecOption ExecuteOption, ref object VariantIn, ref object VariantOut, ref bool Handled)
       {
           if (ExecuteOption == vsCommandExecOption.vsCommandExecOptionDoDefault)
           {
               _dispatchers.DispatchExec(CmdName, ExecuteOption, ref VariantIn, ref VariantOut, ref Handled);
           }
       }
   }
}