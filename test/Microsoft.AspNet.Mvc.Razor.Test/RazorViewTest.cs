// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.Http.Features;
using Microsoft.AspNet.Http.Internal;
using Microsoft.AspNet.Mvc.Abstractions;
using Microsoft.AspNet.Mvc.ModelBinding;
using Microsoft.AspNet.Mvc.Rendering;
using Microsoft.AspNet.Mvc.ViewFeatures;
using Microsoft.AspNet.PageExecutionInstrumentation;
using Microsoft.AspNet.Routing;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.WebEncoders.Testing;
using Moq;
using Xunit;

namespace Microsoft.AspNet.Mvc.Razor
{
    public class RazorViewTest
    {
        private const string LayoutPath = "~/Shared/_Layout.cshtml";

#pragma warning disable 1998
        private readonly RenderAsyncDelegate _nullRenderAsyncDelegate = async writer => { };
#pragma warning restore 1998

        [Fact]
        public async Task RenderAsync_AsPartial_BuffersOutput()
        {
            // Arrange
            TextWriter actual = null;
            var page = new TestableRazorPage(v =>
            {
                actual = v.Output;
                v.HtmlEncoder = new HtmlTestEncoder();
                v.Write("Hello world");
            });
            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            var expected = viewContext.Writer;

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.NotSame(expected, actual);
            Assert.IsAssignableFrom<IBufferedTextWriter>(actual);
            Assert.Equal("HtmlEncode[[Hello world]]", viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_AsPartial_ActivatesViews_WithThePassedInViewContext()
        {
            // Arrange
            var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider());
            var page = new TestableRazorPage(v =>
            {
                // viewData is assigned to ViewContext by the activator
                Assert.Same(viewData, v.ViewContext.ViewData);
            });
            var activator = new Mock<IRazorPageActivator>();
            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                activator.Object,
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());

            var viewContext = CreateViewContext(view);
            var expectedWriter = viewContext.Writer;
            activator
                .Setup(a => a.Activate(page, It.IsAny<ViewContext>()))
                .Callback((IRazorPage p, ViewContext c) =>
                {
                    Assert.Same(c, viewContext);
                    c.ViewData = viewData;
                })
                .Verifiable();

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            activator.Verify();
            Assert.Same(expectedWriter, viewContext.Writer);
        }

        [Fact]
        public async Task ViewContext_ExecutingPagePath_ReturnsPathOfRazorPageBeingExecuted()
        {
            // Arrange
            var pagePath = "/my/view";
            var paths = new List<string>();
            var page = new TestableRazorPage(v =>
            {
                paths.Add(v.ViewContext.ExecutingFilePath);
                Assert.Equal(pagePath, v.ViewContext.View.Path);
            })
            {
                Path = pagePath
            };

            var viewStart = new TestableRazorPage(v =>
            {
                v.Layout = LayoutPath;
                paths.Add(v.ViewContext.ExecutingFilePath);
                Assert.Equal(pagePath, v.ViewContext.View.Path);
            })
            {
                Path = "_ViewStart"
            };

            var layout = new TestableRazorPage(v =>
            {
                v.RenderBodyPublic();
                paths.Add(v.ViewContext.ExecutingFilePath);
                Assert.Equal(pagePath, v.ViewContext.View.Path);
            })
            {
                Path = LayoutPath
            };

            var activator = Mock.Of<IRazorPageActivator>();
            var viewEngine = new Mock<IRazorViewEngine>();
            viewEngine
                .Setup(p => p.GetAbsolutePath("_ViewStart", LayoutPath))
                .Returns(LayoutPath);
            viewEngine
                .Setup(v => v.GetPage(pagePath, LayoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(LayoutPath, layout));
            var view = new RazorView(
                viewEngine.Object,
                activator,
                new[] { viewStart },
                page,
                new HtmlTestEncoder());

            var viewContext = CreateViewContext(view);
            var expectedWriter = viewContext.Writer;

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(new[] { "_ViewStart", pagePath, LayoutPath }, paths);
        }

        [Fact]
        public async Task RenderAsync_AsPartial_ActivatesViews()
        {
            // Arrange
            var page = new TestableRazorPage(v => { });
            var activator = new Mock<IRazorPageActivator>();
            activator
                .Setup(a => a.Activate(page, It.IsAny<ViewContext>()))
                .Verifiable();
            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                activator.Object,
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            activator.Verify();
        }

        [Fact]
        public async Task RenderAsync_AsPartial_ExecutesLayout_ButNotViewStartPages()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var expected = string.Join(
                Environment.NewLine,
                "HtmlEncode[[layout-content",
                "]]HtmlEncode[[page-content]]");
            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = LayoutPath;
                v.Write("page-content");
            });

            var layout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-content" + Environment.NewLine);
                v.RenderBodyPublic();
            });
            var pageFactory = new Mock<IRazorPageFactoryProvider>();
            pageFactory
                .Setup(p => p.CreateFactory(LayoutPath))
                .Returns(new RazorPageFactoryResult(() => layout, new IChangeToken[0]));

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, LayoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(LayoutPath, layout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_CreatesOutputBuffer()
        {
            // Arrange
            TextWriter actual = null;
            var page = new TestableRazorPage(v =>
            {
                actual = v.Output;
            });
            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            var original = viewContext.Writer;

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.IsType<RazorTextWriter>(actual);
            Assert.NotSame(original, actual);
        }

        [Fact]
        public async Task RenderAsync_CopiesBufferedContentToOutput()
        {
            // Arrange
            var page = new TestableRazorPage(v =>
            {
                v.WriteLiteral("Hello world");
            });
            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            var original = viewContext.Writer;

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal("Hello world", original.ToString());
        }

        [Fact]
        public async Task RenderAsync_ActivatesPages()
        {
            // Arrange
            var page = new TestableRazorPage(v =>
            {
                v.WriteLiteral("Hello world");
            });
            var activator = new Mock<IRazorPageActivator>();
            activator
                .Setup(a => a.Activate(page, It.IsAny<ViewContext>()))
                .Verifiable();
            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                activator.Object,
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            activator.Verify();
        }

        [Fact]
        public async Task RenderAsync_ExecutesViewStart()
        {
            // Arrange
            var actualLayoutPath = "";
            var layoutPath = "/Views/_Shared/_Layout.cshtml";
            var viewStart1 = new TestableRazorPage(v =>
            {
                v.Layout = "/fake-layout-path";
            });
            var viewStart2 = new TestableRazorPage(v =>
            {
                v.Layout = layoutPath;
            });
            var page = new TestableRazorPage(v =>
            {
                // This path must have been set as a consequence of running viewStart
                actualLayoutPath = v.Layout;
                // Clear out layout so we don't render it
                v.Layout = null;
            });
            var activator = new Mock<IRazorPageActivator>();
            activator
                .Setup(a => a.Activate(viewStart1, It.IsAny<ViewContext>()))
                .Verifiable();
            activator
                .Setup(a => a.Activate(viewStart2, It.IsAny<ViewContext>()))
                .Verifiable();
            activator
                .Setup(a => a.Activate(page, It.IsAny<ViewContext>()))
                .Verifiable();

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(engine => engine.GetAbsolutePath(/*executingFilePath*/ null, "/fake-layout-path"))
                .Returns("/fake-layout-path");
            viewEngine
                .Setup(engine => engine.GetAbsolutePath(/*executingFilePath*/ null, layoutPath))
                .Returns(layoutPath);

            var view = new RazorView(
                viewEngine.Object,
                activator.Object,
                new[] { viewStart1, viewStart2 },
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            activator.Verify();
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfLayoutPageCannotBeFound_MessageUsesGetPageLocations()
        {
            // Arrange
            var expected = string.Join(
                Environment.NewLine,
                "The layout view 'Does-Not-Exist-Layout' could not be located. The following locations were searched:",
                "path1",
                "path2");

            var layoutPath = "Does-Not-Exist-Layout";
            var page = new TestableRazorPage(v =>
            {
                v.Layout = layoutPath;
            });

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            var activator = new Mock<IRazorPageActivator>();
            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, new[] { "path1", "path2" }))
                .Verifiable();
            viewEngine
                .Setup(v => v.FindPage(viewContext, layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, Enumerable.Empty<string>()))
                .Verifiable();

            // Act
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));

            // Assert
            Assert.Equal(expected, ex.Message);
            viewEngine.Verify();
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfLayoutPageCannotBeFound_MessageUsesFindPageLocations()
        {
            // Arrange
            var expected = string.Join(
                Environment.NewLine,
                "The layout view 'Does-Not-Exist-Layout' could not be located. The following locations were searched:",
                "path1",
                "path2");

            var layoutPath = "Does-Not-Exist-Layout";
            var page = new TestableRazorPage(v =>
            {
                v.Layout = layoutPath;
            });

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            var activator = new Mock<IRazorPageActivator>();
            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, Enumerable.Empty<string>()))
                .Verifiable();
            viewEngine
                .Setup(v => v.FindPage(viewContext, layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, new[] { "path1", "path2" }))
                .Verifiable();

            // Act
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));

            // Assert
            Assert.Equal(expected, ex.Message);
            viewEngine.Verify();
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfLayoutPageCannotBeFound_MessageUsesAllLocations()
        {
            // Arrange
            var expected = string.Join(
                Environment.NewLine,
                "The layout view 'Does-Not-Exist-Layout' could not be located. The following locations were searched:",
                "path1",
                "path2",
                "path3",
                "path4");

            var layoutPath = "Does-Not-Exist-Layout";
            var page = new TestableRazorPage(v =>
            {
                v.Layout = layoutPath;
            });

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            var activator = new Mock<IRazorPageActivator>();
            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, new[] { "path1", "path2" }))
                .Verifiable();
            viewEngine
                .Setup(v => v.FindPage(viewContext, layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, new[] { "path3", "path4" }))
                .Verifiable();

            // Act
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));

            // Assert
            Assert.Equal(expected, ex.Message);
            viewEngine.Verify();
        }

        [Fact]
        public async Task RenderAsync_ExecutesLayoutPages()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var htmlEncodedNewLine = htmlEncoder.Encode(Environment.NewLine);
            var expected = "HtmlEncode[[layout-content" +
                           Environment.NewLine +
                           "]]head-content" +
                           htmlEncodedNewLine +
                           "body-content" +
                           htmlEncodedNewLine +
                           "foot-content";

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.WriteLiteral("body-content");
                v.Layout = LayoutPath;
                v.DefineSection("head", async writer =>
                {
                    await writer.WriteAsync("head-content");
                });
                v.DefineSection("foot", async writer =>
                {
                    await writer.WriteAsync("foot-content");
                });
            });
            var layout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-content" + Environment.NewLine);
                v.Write(v.RenderSection("head"));
                v.Write(Environment.NewLine);
                v.RenderBodyPublic();
                v.Write(Environment.NewLine);
                v.Write(v.RenderSection("foot"));
            });
            var activator = new Mock<IRazorPageActivator>();
            activator
                .Setup(a => a.Activate(page, It.IsAny<ViewContext>()))
                .Verifiable();
            activator
                .Setup(a => a.Activate(layout, It.IsAny<ViewContext>()))
                .Verifiable();
            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, LayoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(LayoutPath, layout))
                .Verifiable();

            var view = new RazorView(
                viewEngine.Object,
                activator.Object,
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            // Verify the activator was invoked for the primary page and layout page.
            activator.Verify();
            Assert.Equal(expected, viewContext.Writer.ToString());
            viewEngine.Verify();
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfSectionsWereDefinedButNotRendered()
        {
            // Arrange
            var page = new TestableRazorPage(v =>
            {
                v.DefineSection("head", _nullRenderAsyncDelegate);
                v.Layout = LayoutPath;
                v.DefineSection("foot", _nullRenderAsyncDelegate);
            });
            var layout = new TestableRazorPage(v =>
            {
                v.RenderBodyPublic();
            })
            {
                Path = LayoutPath
            };
            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, LayoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(LayoutPath, layout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));
            Assert.Equal("The following sections have been defined but have not been rendered by the page "
                + $"at '{LayoutPath}': 'head, foot'.", ex.Message);
        }

        [Fact]
        public async Task RenderAsync_SucceedsIfNestedSectionsAreRendered()
        {
            // Arrange
            var expected = string.Join(
                Environment.NewLine,
                "layout-section-content",
                "page-section-content");

            var htmlEncoder = new HtmlTestEncoder();
            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout1.cshtml";
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteAsync("page-section-content");
                });
            });
            var nestedLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout2.cshtml";
                v.RenderBodyPublic();
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("layout-section-content");
                    await v.RenderSectionAsync("foo");
                });
            })
            {
                Path = "/Shared/Layout1.cshtml"
            };
            var baseLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.RenderBodyPublic();
                v.RenderSection("foo");
            })
            {
                Path = "/Shared/Layout2.cshtml"
            };

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, "~/Shared/Layout1.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout1.cshtml", nestedLayout));
            viewEngine
                .Setup(v => v.GetPage("/Shared/Layout1.cshtml", "~/Shared/Layout2.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout2.cshtml", baseLayout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_SucceedsIfRenderBodyIsNotInvoked_ButAllSectionsAreRendered()
        {
            // Arrange
            var expected = string.Join(
                Environment.NewLine,
                "layout-section-content",
                "page-section-content");

            var htmlEncoder = new HtmlTestEncoder();
            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "NestedLayout";
                v.WriteLiteral("Page body content that will not be written");
                v.DefineSection("sectionA", async writer =>
                {
                    await writer.WriteAsync("page-section-content");
                });
            });
            var nestedLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "Layout";
                v.WriteLiteral("Nested layout content that will not be written");
                v.DefineSection("sectionB", async writer =>
                {
                    await writer.WriteLineAsync("layout-section-content");
                    await v.RenderSectionAsync("sectionA");
                });
            });
            nestedLayout.Path = "NestedLayout";
            var baseLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.RenderSection("sectionB");
            });
            baseLayout.Path = "Layout";

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, "NestedLayout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("NestedLayout", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "NestedLayout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("NestedLayout", nestedLayout));
            viewEngine
                .Setup(v => v.GetPage("NestedLayout", "Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("Layout", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("Layout", baseLayout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_WithNestedSections_ThrowsIfSectionsWereDefinedButNotRendered()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout1.cshtml";
                v.WriteLiteral("BodyContent");
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("foo-content");
                });
            });
            var nestedLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout2.cshtml";
                v.Write("NestedLayout" + Environment.NewLine);
                v.RenderBodyPublic();
                v.DefineSection("foo", async _ =>
                {
                    await v.RenderSectionAsync("foo");
                });
            })
            {
                Path = "/Shared/Layout1.cshtml"
            };
            var baseLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("BaseLayout" + Environment.NewLine);
                v.RenderBodyPublic();
            })
            {
                Path = "/Shared/Layout2.cshtml"
            };

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(v => v.GetPage(/*executingFilePath*/ null, "~/Shared/Layout1.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout1.cshtml", nestedLayout));
            viewEngine
                .Setup(v => v.GetPage("/Shared/Layout1.cshtml", "~/Shared/Layout2.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout2.cshtml", baseLayout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));
            Assert.Equal("The following sections have been defined but have not been rendered by the page at "
                + "'/Shared/Layout1.cshtml': 'foo'.", ex.Message);
        }

        [Fact]
        public async Task RenderAsync_WithNestedSectionsOfTheSameName_ThrowsIfSectionsWereDefinedButNotRendered()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout1.cshtml";
                v.WriteLiteral("BodyContent");
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("foo-content");
                });
            })
            {
                Path = "Page"
            };

            var nestedLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout2.cshtml";
                v.Write("NestedLayout" + Environment.NewLine);
                v.RenderBodyPublic();
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("dont-render-inner-foo");
                });
            })
            {
                Path = "/Shared/Layout1.cshtml"
            };

            var baseLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("BaseLayout" + Environment.NewLine);
                v.RenderBodyPublic();
                v.RenderSection("foo");
            })
            {
                Path = "/Shared/Layout2.cshtml"
            };

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage("Page", "~/Shared/Layout1.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout1.cshtml", nestedLayout));
            viewEngine
                .Setup(p => p.GetPage("/Shared/Layout1.cshtml", "~/Shared/Layout2.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout2.cshtml", baseLayout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));
            Assert.Equal("The following sections have been defined but have not been rendered by the page at " +
                "'/Shared/Layout1.cshtml': 'foo'.", ex.Message);
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfBodyWasNotRendered()
        {
            // Arrange
            var page = new TestableRazorPage(v =>
            {
                v.Layout = LayoutPath;
            });
            var layout = new TestableRazorPage(v =>
            {
            })
            {
                Path = LayoutPath
            };
            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(/*executingFilePath*/ null, LayoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(LayoutPath, layout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));
            Assert.Equal($"RenderBody has not been called for the page at '{LayoutPath}'.", ex.Message);
        }

        [Fact]
        public async Task RenderAsync_ExecutesNestedLayoutPages()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var expected = "HtmlEncode[[layout-2" +
                           Environment.NewLine +
                           "]]bar-content" +
                           Environment.NewLine +
                           "HtmlEncode[[layout-1" +
                           Environment.NewLine +
                           "]]foo-content" +
                           Environment.NewLine +
                           "body-content";

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("foo-content");
                });
                v.Layout = "~/Shared/Layout1.cshtml";
                v.WriteLiteral("body-content");
            });
            var layout1 = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-1" + Environment.NewLine);
                v.Write(v.RenderSection("foo"));
                v.DefineSection("bar", writer => writer.WriteLineAsync("bar-content"));
                v.RenderBodyPublic();
                v.Layout = "~/Shared/Layout2.cshtml";
            });
            layout1.Path = "~/Shared/Layout1.cshtml";

            var layout2 = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-2" + Environment.NewLine);
                v.Write(v.RenderSection("bar"));
                v.RenderBodyPublic();
            });
            layout2.Path = "~/Shared/Layout2.cshtml";

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(/*executingFilePath*/ null, "~/Shared/Layout1.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout1.cshtml", layout1));
            viewEngine
                .Setup(p => p.GetPage("~/Shared/Layout1.cshtml", "~/Shared/Layout2.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout2.cshtml", layout2));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_ExecutesNestedLayoutPages_WithRelativePaths()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var expected =
                "HtmlEncode[[layout-2" + Environment.NewLine +
                "]]bar-content" + Environment.NewLine +
                "HtmlEncode[[layout-1" + Environment.NewLine +
                "]]foo-content" + Environment.NewLine +
                "body-content";

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("foo-content");
                });
                v.Layout = "Layout1.cshtml";
                v.WriteLiteral("body-content");
            })
            {
                Path = "~/Shared/Page.cshtml",
            };

            var layout1 = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-1" + Environment.NewLine);
                v.Write(v.RenderSection("foo"));
                v.DefineSection("bar", writer => writer.WriteLineAsync("bar-content"));
                v.RenderBodyPublic();
                v.Layout = "Layout2.cshtml";
            })
            {
                Path = "~/Shared/Layout1.cshtml",
            };

            var layout2 = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-2" + Environment.NewLine);
                v.Write(v.RenderSection("bar"));
                v.RenderBodyPublic();
            })
            {
                Path = "~/Shared/Layout2.cshtml",
            };

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage("~/Shared/Page.cshtml", "Layout1.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout1.cshtml", layout1));
            viewEngine
                .Setup(p => p.GetPage("~/Shared/Layout1.cshtml", "Layout2.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout2.cshtml", layout2));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_Throws_IfLayoutPageReferencesSelf()
        {
            // Arrange
            var expectedMessage = "A circular layout reference was detected when rendering " +
                "'Shared/Layout.cshtml'. The layout page 'Shared/Layout.cshtml' has already been rendered.";
            var page = new TestableRazorPage(v =>
            {
                v.Layout = "_Layout";
            });
            var layout = new TestableRazorPage(v =>
            {
                v.Layout = "_Layout";
                v.RenderBodyPublic();
            });
            layout.Path = "Shared/Layout.cshtml";

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(It.IsAny<string>(), "_Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("_Layout", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "_Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("_Layout", layout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
        }

        [Fact]
        public async Task RenderAsync_Throws_IfNestedLayoutPagesResultInCyclicReferences()
        {
            // Arrange
            var expectedMessage = "A circular layout reference was detected when rendering " +
                "'/Shared/Layout2.cshtml'. The layout page 'Shared/_Layout.cshtml' has already been rendered.";
            var page = new TestableRazorPage(v =>
            {
                v.Layout = "_Layout";
            });
            var layout1 = new TestableRazorPage(v =>
            {
                v.Layout = "_Layout2";
                v.RenderBodyPublic();
            });
            layout1.Path = "Shared/_Layout.cshtml";

            var layout2 = new TestableRazorPage(v =>
            {
                v.Layout = "_Layout";
                v.RenderBodyPublic();
            });
            layout2.Path = "/Shared/Layout2.cshtml";

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(It.IsAny<string>(), "_Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("_Layout1", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "_Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("_Layout", layout1));
            viewEngine
                .Setup(p => p.GetPage("Shared/_Layout.cshtml", "_Layout2", /*isMainPage*/ false))
                .Returns(new RazorPageResult("_Layout2", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "_Layout2", /*isMainPage*/ false))
                .Returns(new RazorPageResult("_Layout2", layout2));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));

            // Assert
            Assert.Equal(expectedMessage, exception.Message);
        }

        [Fact]
        public async Task RenderAsync_ExecutesNestedLayoutsWithNestedSections()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var expected = "HtmlEncode[[BaseLayout" +
                           Environment.NewLine +
                           "]]HtmlEncode[[NestedLayout" +
                           Environment.NewLine +
                           "]]BodyContent" +
                           "foo-content" +
                           Environment.NewLine +
                           Environment.NewLine;

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout1.cshtml";
                v.WriteLiteral("BodyContent");
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync("foo-content");
                });
            });
            var nestedLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "~/Shared/Layout2.cshtml";
                v.Write("NestedLayout" + Environment.NewLine);
                v.RenderBodyPublic();
                v.DefineSection("foo", async writer =>
                {
                    await writer.WriteLineAsync(htmlEncoder.Encode(v.RenderSection("foo").ToString()));
                });
            });
            nestedLayout.Path = "~/Shared/Layout1.cshtml";

            var baseLayout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("BaseLayout" + Environment.NewLine);
                v.RenderBodyPublic();
                v.Write(v.RenderSection("foo"));
            });
            baseLayout.Path = "~/Shared/Layout2.cshtml";

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(/*executingFilePath*/ null, "~/Shared/Layout1.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout1.cshtml", nestedLayout));
            viewEngine
                .Setup(p => p.GetPage("~/Shared/Layout1.cshtml", "~/Shared/Layout2.cshtml", /*isMainPage*/ false))
                .Returns(new RazorPageResult("~/Shared/Layout2.cshtml", baseLayout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_DoesNotCopyContentOnceRazorTextWriterIsNoLongerBuffering()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var expected = "HtmlEncode[[layout-1" +
                           Environment.NewLine +
                           "]]body content" +
                           Environment.NewLine +
                           "section-content-1" +
                           Environment.NewLine +
                           "section-content-2";

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Layout = "layout-1";
                v.WriteLiteral("body content" + Environment.NewLine);
                v.DefineSection("foo", async _ =>
                {
                    v.WriteLiteral("section-content-1" + Environment.NewLine);
                    await v.FlushAsync();
                    v.WriteLiteral("section-content-2");
                });
            });

            var layout1 = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-1" + Environment.NewLine);
                v.RenderBodyPublic();
                v.Write(v.RenderSection("foo"));
            });

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(/*executingFilePath*/ null, "layout-1", /*isMainPage*/ false))
                .Returns(new RazorPageResult("layout-1", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "layout-1", /*isMainPage*/ false))
                .Returns(new RazorPageResult("layout-1", layout1));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task FlushAsync_DoesNotThrowWhenInvokedInsideOfASection()
        {
            // Arrange
            var htmlEncoder = new HtmlTestEncoder();
            var expected = "HtmlEncode[[layout-1" +
                           Environment.NewLine +
                           "]]section-content-1" +
                           Environment.NewLine +
                           "section-content-2";

            var page = new TestableRazorPage(v =>
           {
               v.HtmlEncoder = htmlEncoder;
               v.Layout = "layout-1";
               v.DefineSection("foo", async _ =>
               {
                   v.WriteLiteral("section-content-1" + Environment.NewLine);
                   await v.FlushAsync();
                   v.WriteLiteral("section-content-2");
               });
           });

            var layout1 = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = htmlEncoder;
                v.Write("layout-1" + Environment.NewLine);
                v.RenderBodyPublic();
                v.Write(v.RenderSection("foo"));
            });

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage(/*executingFilePath*/ null, "layout-1", /*isMainPage*/ false))
                .Returns(new RazorPageResult("layout-1", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "layout-1", /*isMainPage*/ false))
                .Returns(new RazorPageResult("layout-1", layout1));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfLayoutIsSpecifiedWhenNotBuffered()
        {
            // Arrange
            var expected = "Layout page '/Views/TestPath/Test.cshtml' cannot be rendered" +
                " after 'FlushAsync' has been invoked.";
            var page = new TestableRazorPage(v =>
            {
                v.Path = "/Views/TestPath/Test.cshtml";
                v.WriteLiteral("before-flush" + Environment.NewLine);
                v.FlushAsync().Wait();
                v.Layout = "test-layout";
                v.WriteLiteral("after-flush");
            });

            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));
            Assert.Equal(expected, ex.Message);
        }

        [Fact]
        public async Task RenderAsync_ThrowsIfFlushWasInvokedInsideRenderedSectionAndLayoutWasSet()
        {
            // Arrange
            var expected = "Layout page '/Views/TestPath/Test.cshtml' cannot be rendered" +
                " after 'FlushAsync' has been invoked.";
            var page = new TestableRazorPage(v =>
            {
                v.Path = "/Views/TestPath/Test.cshtml";
                v.HtmlEncoder = new HtmlTestEncoder();
                v.DefineSection("foo", async writer =>
                {
                    writer.WriteLine("foo-content");
                    await v.FlushAsync();
                });
                v.Layout = "~/Shared/Layout1.cshtml";
                v.WriteLiteral("body-content");
            });
            var layoutPage = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = new HtmlTestEncoder();
                v.Write("layout-1" + Environment.NewLine);
                v.Write(v.RenderSection("foo"));
                v.DefineSection("bar", writer => writer.WriteLineAsync("bar-content"));
                v.RenderBodyPublic();
                v.Layout = "~/Shared/Layout2.cshtml";
            });
            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            var layoutPath = "~/Shared/Layout1.cshtml";
            viewEngine
                .Setup(p => p.GetPage("/Views/TestPath/Test.cshtml", layoutPath, /*isMainPage*/ false))
                .Returns(new RazorPageResult(layoutPath, layoutPage));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act and Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => view.RenderAsync(viewContext));
            Assert.Equal(expected, ex.Message);
        }

        [Fact]
        public async Task RenderAsync_UsesPageExecutionFeatureFromRequest_ToWrapWriter()
        {
            // Arrange
            var pageWriter = CreateBufferedWriter();
            var layoutWriter = CreateBufferedWriter();

            var layoutExecuted = false;
            var count = -1;
            var feature = new Mock<IPageExecutionListenerFeature>(MockBehavior.Strict);
            feature
                .Setup(f => f.DecorateWriter(It.IsAny<RazorTextWriter>()))
                .Returns(() =>
                {
                    count++;
                    if (count == 0)
                    {
                        return pageWriter;
                    }
                    else if (count == 1)
                    {
                        return layoutWriter;
                    }
                    throw new Exception();
                })
                .Verifiable();

            var pageContext = Mock.Of<IPageExecutionContext>();
            feature
                .Setup(f => f.GetContext("/MyPage.cshtml", pageWriter))
                .Returns(pageContext)
                .Verifiable();

            var layoutContext = Mock.Of<IPageExecutionContext>();
            feature
                .Setup(f => f.GetContext("/Layout.cshtml", layoutWriter))
                .Returns(layoutContext)
                .Verifiable();

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = new HtmlTestEncoder();
                v.Layout = "Layout";
                Assert.Same(pageWriter, v.Output);
                Assert.Same(pageContext, v.PageExecutionContext);
            });
            page.Path = "/MyPage.cshtml";

            var layout = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = new HtmlTestEncoder();
                Assert.Same(layoutWriter, v.Output);
                Assert.Same(layoutContext, v.PageExecutionContext);
                v.RenderBodyPublic();

                layoutExecuted = true;
            });
            layout.Path = "/Layout.cshtml";

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(p => p.GetPage("/MyPage.cshtml", "Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("Layout", Enumerable.Empty<string>()));
            viewEngine
                .Setup(p => p.FindPage(It.IsAny<ActionContext>(), "Layout", /*isMainPage*/ false))
                .Returns(new RazorPageResult("/Layout.cshtml", layout));

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            viewContext.HttpContext.Features.Set<IPageExecutionListenerFeature>(feature.Object);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            feature.Verify();
            Assert.True(layoutExecuted);
        }

        [Fact]
        public async Task RenderAsync_UsesPageExecutionFeatureFromRequest_ToGetExecutionContext()
        {
            // Arrange
            var writer = new StringWriter();
            var executed = false;
            var feature = new Mock<IPageExecutionListenerFeature>(MockBehavior.Strict);

            var pageContext = Mock.Of<IPageExecutionContext>();
            feature.Setup(f => f.GetContext("/MyPartialPage.cshtml", It.IsAny<RazorTextWriter>()))
                    .Returns(pageContext)
                    .Verifiable();

            feature.Setup(f => f.DecorateWriter(It.IsAny<RazorTextWriter>()))
                   .Returns((RazorTextWriter r) => r)
                   .Verifiable();

            var page = new TestableRazorPage(v =>
            {
                v.HtmlEncoder = new HtmlTestEncoder();
                Assert.IsType<RazorTextWriter>(v.Output);
                Assert.Same(pageContext, v.PageExecutionContext);
                executed = true;

                v.Write("Hello world");
            });
            page.Path = "/MyPartialPage.cshtml";

            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);
            viewContext.Writer = writer;
            viewContext.HttpContext.Features.Set<IPageExecutionListenerFeature>(feature.Object);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            feature.Verify();
            Assert.True(executed);
            Assert.Equal("HtmlEncode[[Hello world]]", viewContext.Writer.ToString());
        }

        [Fact]
        public async Task RenderAsync_DoesNotSetExecutionContextWhenListenerIsNotRegistered()
        {
            // Arrange
            var executed = false;
            var page = new TestableRazorPage(v =>
            {
                Assert.Null(v.PageExecutionContext);
                executed = true;
            });

            var view = new RazorView(
                Mock.Of<IRazorViewEngine>(),
                Mock.Of<IRazorPageActivator>(),
                new IRazorPage[0],
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.True(executed);
        }

        [Fact]
        public async Task RenderAsync_CopiesLayoutPropertyFromViewStart()
        {
            // Arrange
            var expectedViewStart = "Layout1";
            var expectedPage = "Layout2";
            string actualViewStart = null;
            string actualPage = null;
            var page = new TestableRazorPage(v =>
            {
                actualPage = v.Layout;
                // Clear it out because we don't care about rendering the layout in this test.
                v.Layout = null;
            });
            var viewStart1 = new TestableRazorPage(v =>
            {
                v.Layout = expectedViewStart;
            });
            var viewStart2 = new TestableRazorPage(v =>
            {
                actualViewStart = v.Layout;
                v.Layout = expectedPage;
            });
            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(engine => engine.GetAbsolutePath(/*executingFilePath*/ null, expectedViewStart))
                .Returns(expectedViewStart);
            viewEngine
                .Setup(engine => engine.GetAbsolutePath(/*executingFilePath*/ null, expectedPage))
                .Returns(expectedPage);

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new[] { viewStart1, viewStart2 },
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expectedViewStart, actualViewStart);
            Assert.Equal(expectedPage, actualPage);
        }

        [Fact]
        public async Task RenderAsync_CopiesLayoutPropertyFromViewStart_WithRelativePaths()
        {
            // Arrange
            var expectedViewStart = "~/_Layout.cshtml";
            var expectedPage = "~/Home/_Layout.cshtml";
            string actualViewStart = null;
            string actualPage = null;
            var page = new TestableRazorPage(v =>
            {
                actualPage = v.Layout;

                // Clear it out because we don't care about rendering the layout in this test.
                v.Layout = null;
            });

            var viewStart1 = new TestableRazorPage(v =>
            {
                v.Layout = "_Layout.cshtml";
            })
            {
                Path = "~/_ViewStart.cshtml",
            };

            var viewStart2 = new TestableRazorPage(v =>
            {
                actualViewStart = v.Layout;
                v.Layout = "_Layout.cshtml";
            })
            {
                Path = "~/Home/_ViewStart.cshtml",
            };

            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(engine => engine.GetAbsolutePath("~/_ViewStart.cshtml", "_Layout.cshtml"))
                .Returns("~/_Layout.cshtml");
            viewEngine
                .Setup(engine => engine.GetAbsolutePath("~/Home/_ViewStart.cshtml", "_Layout.cshtml"))
                .Returns("~/Home/_Layout.cshtml");

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new[] { viewStart1, viewStart2 },
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expectedViewStart, actualViewStart);
            Assert.Equal(expectedPage, actualPage);
        }

        [Fact]
        public async Task ResettingLayout_InViewStartCausesItToBeResetInPage()
        {
            // Arrange
            var expected = "Layout";
            string actual = null;

            var page = new TestableRazorPage(v =>
            {
                Assert.Null(v.Layout);
            });
            var viewStart1 = new TestableRazorPage(v =>
            {
                v.Layout = expected;
            });
            var viewStart2 = new TestableRazorPage(v =>
            {
                actual = v.Layout;
                v.Layout = null;
            });
            var viewEngine = new Mock<IRazorViewEngine>(MockBehavior.Strict);
            viewEngine
                .Setup(engine => engine.GetAbsolutePath(/*executingFilePath*/ null, "Layout"))
                .Returns("Layout");
            viewEngine
                .Setup(engine => engine.GetAbsolutePath(/*executingFilePath*/ null, /*pagePath*/ null))
                .Returns<string>(null);

            var view = new RazorView(
                viewEngine.Object,
                Mock.Of<IRazorPageActivator>(),
                new[] { viewStart1, viewStart2 },
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, actual);
        }

        [Fact]
        public async Task RenderAsync_RendersViewStartsInOrderInWhichTheyAreSpecified()
        {
            // Arrange
            var expected = string.Join(
                Environment.NewLine,
                new[]
                {
                    "ViewStart1",
                    "ViewStart2",
                    "Page",
                });
            var page = new TestableRazorPage(v =>
            {
                v.WriteLiteral("Page");
            });
            var viewStart1 = new TestableRazorPage(v =>
            {
                v.WriteLiteral("ViewStart1" + Environment.NewLine);
            });
            var viewStart2 = new TestableRazorPage(v =>
            {
                v.WriteLiteral("ViewStart2" + Environment.NewLine);
            });
            var viewEngine = Mock.Of<IRazorViewEngine>();

            var view = new RazorView(
                viewEngine,
                Mock.Of<IRazorPageActivator>(),
                new[] { viewStart1, viewStart2 },
                page,
                new HtmlTestEncoder());
            var viewContext = CreateViewContext(view);

            // Act
            await view.RenderAsync(viewContext);

            // Assert
            Assert.Equal(expected, viewContext.Writer.ToString());
        }

        private static TextWriter CreateBufferedWriter()
        {
            var mockWriter = new Mock<TextWriter>();
            var bufferedWriter = mockWriter.As<IBufferedTextWriter>();
            bufferedWriter.SetupGet(b => b.IsBuffering)
                          .Returns(true);
            return mockWriter.Object;
        }

        private static ViewContext CreateViewContext(RazorView view)
        {
            var httpContext = new DefaultHttpContext();
            var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
            return new ViewContext(
                actionContext,
                view,
                new ViewDataDictionary(new EmptyModelMetadataProvider()),
                Mock.Of<ITempDataDictionary>(),
                new StringWriter(),
                new HtmlHelperOptions());
        }

        private class TestableRazorPage : RazorPage
        {
            private readonly Action<TestableRazorPage> _executeAction;

            public TestableRazorPage(Action<TestableRazorPage> executeAction)
            {
                _executeAction = executeAction;
                HtmlEncoder = new HtmlTestEncoder();
            }

            public void RenderBodyPublic()
            {
                Write(RenderBody());
            }

            public override Task ExecuteAsync()
            {
                _executeAction(this);
                return Task.FromResult(0);
            }
        }
    }
}