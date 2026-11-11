# <img src="source/Resources/DevKit_32.png" width="32" height="32" alt="" /> Revit DevKit

[![Revit 2019-2024](https://img.shields.io/badge/Revit-2019--2024-blue.svg)](https://github.com/Samveyil/RevitDevKit)
[![Visual Studio 2022](https://img.shields.io/badge/Visual%20Studio-2022-blue)](https://github.com/Samveyil/RevitDevKit)
[![Nuke](https://img.shields.io/badge/Nuke-Build-blue)](https://nuke.build/)
[![License MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/Samveyil/BIMPlugins.ExtStorage/blob/master/LICENSE)
[![README на русском](https://img.shields.io/badge/README-%D0%BD%D0%B0%20%D1%80%D1%83%D1%81%D1%81%D0%BA%D0%BE%D0%BC-blue)](README.ru.md)
[![Release](https://img.shields.io/github/v/release/Samveyil/RevitDevKit?label=Release&color=blue)](https://github.com/Samveyil/RevitDevKit/releases/latest)

A Revit 2019–2024 add-in that helps while developing plugins. It has a C# script editor, runs commands from a DLL without restarting Revit, updates ribbon buttons from a new build and has a console for output.

The add-in's interface is in Russian; the names of its menus and buttons are given below with a translation.

The Add-Ins tab gets a DevKit panel with a pulldown button:

- Script Editor opens the script editor;
- DLL Runner opens a window with the commands and applications of a DLL, the add-ins Revit loads at startup and their DLLs;
- Run Again repeats the last run;
- Console shows the console pane.

## Script Editor

![Script Editor](assets/script-editor.png)

Write code, press F5, and it runs in the current document. A few lines are enough, as in the screenshot: the add-in wraps them in a class and adds the main `using` directives for System, LINQ and the Revit API. If the file declares a class, it is compiled as it is, so you can try a regular `IExternalCommand` or a class with a `Main(Document doc, UIApplication uiapp)` method.

An assembly that Revit does not load can be referenced with `#r`:

```csharp
#r "D:\Libs\MyHelpers.dll"
```

The **Примеры** (Samples) menu has a few ready-made scripts. The **Автотранзакция** (auto transaction) checkbox wraps the run in a transaction, for scripts that change the model without opening one themselves. If a script does not compile, the errors go to the console and the caret moves to the first of them.

The editor suggests as you type: after a dot it lists the members of the type, after `(` it shows the method's overloads with parameter descriptions from the Revit API documentation. After `<` of a generic method the list keeps only the types that fit: `All<` offers element classes, and `walls.OfType<` puts the subclasses of the collection's element first. Brackets `()`, `[]`, `{}`, `<>` of generics and quotes close by themselves, and Enter between `{}` opens a block.

Alt with a mouse selection, or Shift+Alt with the arrow keys, puts a caret on several lines at once, as in Visual Studio. Typed text goes into every line, Tab and Shift+Tab shift them together, and the arrow keys move all the carets. In this mode no suggestions open and no brackets close.

Indentation is coloured by level, as the Indent Rainbow extension for Visual Studio does, with thin guide lines. Indentation that is not a multiple of four spaces is red, so stray spaces on blank lines show too.

| Keys | |
|---|---|
| F5, Ctrl+Enter | run |
| Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+W | new tab, open, save, close |
| Ctrl+Space | suggestions; the list also opens by itself after a dot and on the first letter of a name |
| Ctrl+Shift+Space | method overloads with parameter descriptions; the window also opens by itself after `(` and `,` |
| Alt + mouse, Shift+Alt+arrows | carets on several lines |
| Ctrl+D | duplicate the line |
| Ctrl+L | clear the console |

Short scripts can use:

| | |
|---|---|
| `Doc`, `UIDoc`, `UIApp`, `App`, `ActiveView` | the document, the application, the active view |
| `Print()`, `Ok()`, `Warn()`, `Fail()` | console output in the normal colour, green, orange and red |
| `Table(items, x => ...)` | a list printed as a table |
| `Tx("Name", () => ...)` | a transaction |
| `All<T>()`, `Instances(category)`, `Types(category)` | elements by class or category, optionally limited to a view |
| `Selection`, `S0`, `S1`, `S(i)` | the selected elements |
| `Dump(element)` | all parameters of an element |
| `Compare(a, b, ...)` | differences in the parameters of 2–5 elements |

```csharp
var walls = All<Wall>();
Print($"Walls in the model: {walls.Count}");

Tx("Mark walls", () =>
{
    foreach (var wall in walls)
        wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("checked");
});
```

In the output of `Compare`, `-нет-` (none) means the element has no such parameter, `-` means the parameter has no value, and `-пусто-` (empty) means an empty string.

## DLL Runner

### Commands

<img src="assets/dll-runner-commands.png" width="520" alt="DLL Runner, the Commands tab" />

Add a built DLL, and its `IExternalCommand` classes appear in the list, grouped by namespace. A double click or F5 runs a command. The DLL file is not locked, so the project can be rebuilt without closing Revit. The command is taken from the DLL on disk every time, so after a build just run it again. The **Обновить** (Refresh) button is needed only when new commands appear.

The window closes when a command runs. To run the same command again after changes, there is no need to open DLL Runner: build the project and press Run Again on the ribbon, and the command runs from the fresh DLL. Run Again can be given a keyboard shortcut in Revit.

### Applications

<img src="assets/dll-runner-apps.png" width="520" alt="DLL Runner, the Applications tab" />

Here you can see what an add-in's buttons will look like after changes, without restarting Revit. The add-in is installed as usual with an `.addin` file, and the DLL from the project's build folder is added here. After changes, build the project and press **Обновить** (Refresh): the add-in's buttons on the ribbon change their texts, tooltips, icons and `ShowText`. New buttons and panels appear, removed ones disappear, and a button that became a pulldown is replaced in the same place.

The old buttons keep running commands from the version Revit loaded, and the new ones run code from the fresh build. After Revit restarts, the ribbon is again what the `.addin` creates. The **Вернуть как было** (Restore) button brings it back without a restart.

The DLL may have a different name than the installed one, for example `Dbg_MyAddin.dll` instead of `MyAddin.dll`.

### Startup

<img src="assets/dll-runner-startup.png" width="520" alt="DLL Runner, the Startup tab" />

This tab lists the `.addin` files of the running Revit version: the current user's from `%AppData%\Autodesk\Revit\Addins\<year>` and those for all users from `%ProgramData%\Autodesk\Revit\Addins\<year>`. Clearing a checkbox renames `X.addin` to `X.addin.disable`, so Revit does not load the add-in on its next start, and ticking it renames the file back.

Under each name is what the file contains and its DLL, and in red what is wrong: a DLL that is not found or an `.addin` that cannot be read. Files that Windows does not let you rename, usually installers' files for all users, have a lock: changing them needs administrator rights. Before DevKit turns itself off, it asks.

### Dependencies

<img src="assets/dll-runner-dependencies.png" width="520" alt="DLL Runner, the Dependencies tab" />

This tab finds DLLs that several add-ins of the running Revit version, or an add-in and Revit itself, bring in different versions, and shows which of them really clash in the one Revit process they all share:

- **Red**: an add-in gets an older assembly version than it was built against, and may fail. That happens to a DLL without a strong name, where the first copy loaded goes to everyone, and to one that `Revit.exe.config` redirects down to Revit's version. A DLL without a strong name in different versions is a conflict as a whole: which copy loads first depends on the order the add-ins load, so next time another add-in may get the older one.
- **Orange**: an add-in gets a newer version (fine if the library only added things), another file of the same assembly version, or a newer version Revit redirects it to. One copy is shared, which is usually harmless.
- With **Только конфликты** (conflicts only) cleared, signed DLLs of different versions show too: .NET loads each add-in its own version, side by side. Revit's redirects count only for the versions their `oldVersion` names.

The copy loaded in this session is marked. A right click on a DLL or an add-in shows it in Explorer or copies its path. An add-in's DLLs are the ones in the folder of its assembly, read from their headers without loading them. Language resources (folders such as `en-US` and `*.resources.dll`) are left out: they are the same DLL in another language. The copy button puts a markdown report on the clipboard.

## Console

![Console](assets/console.png)

Everything that scripts and DLL commands write goes here: `Console.WriteLine`, `Debug.WriteLine`, `Trace` and exceptions with their call stacks. The console opens as a separate Revit pane and is also shown at the bottom of the script editor. It can be cleared and copied, and checkboxes turn on timestamps and debug lines.

## License

This project is [licensed](https://github.com/Samveyil/RevitDevKit/blob/master/LICENSE.txt) under the [MIT License](https://en.wikipedia.org/wiki/MIT_License).

---

Do you like this project? Please [star this project on GitHub](https://github.com/Samveyil/RevitDevKit/stargazers)!
