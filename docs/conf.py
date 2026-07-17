project = "RP2040Sharp"
copyright = "2026, Iván Montiel Cardona"
author = "Iván Montiel Cardona"
release = "1.0.0"
version = "1.0"

extensions = [
    "myst_parser",
    "sphinx_copybutton",
    "sphinx_design",
    "sphinx.ext.intersphinx",
]

source_suffix = {
    ".rst": "restructuredtext",
    ".md": "markdown",
}

templates_path = ["_templates"]
exclude_patterns = ["_build", ".venv", "Thumbs.db", ".DS_Store"]

# Served as a sub-path of the shared PyMCU docs site.
html_baseurl = "https://docs.pymcu.org/rp2040sharp/"

# ---------------------------------------------------------------------------
# MyST extensions
# ---------------------------------------------------------------------------
myst_enable_extensions = [
    "colon_fence",
    "deflist",
    "tasklist",
    "attrs_inline",
]
myst_heading_anchors = 4

# ---------------------------------------------------------------------------
# HTML / PyData theme (matches the PyMCU docs site)
# ---------------------------------------------------------------------------
html_theme = "pydata_sphinx_theme"
html_static_path = ["_static"]
html_css_files = ["css/custom.css"]
html_title = "RP2040Sharp"

html_theme_options = {
    "navbar_align": "left",
    "navbar_end": ["navbar-icon-links", "theme-switcher"],
    "secondary_sidebar_items": ["page-toc", "edit-this-page"],
    "show_prev_next": True,
    "navigation_with_keys": True,
    "footer_start": ["copyright"],
    "footer_end": ["sphinx-version"],
    "pygments_light_style": "friendly",
    "pygments_dark_style": "monokai",
    "header_links_before_dropdown": 6,
    "navigation_depth": 3,
    "show_nav_level": 1,
    "icon_links": [
        {
            "name": "PyMCU docs",
            "url": "https://docs.pymcu.org",
            "icon": "fa-solid fa-house",
        },
        {
            "name": "GitHub",
            "url": "https://github.com/PyMCU/RP2040Sharp",
            "icon": "fa-brands fa-github",
        },
        {
            "name": "NuGet",
            "url": "https://www.nuget.org/packages/RP2040Sharp",
            "icon": "fa-solid fa-cube",
        },
    ],
}

html_sidebars = {
    "**": ["sidebar-nav-bs"],
}

# "Edit this page" links point at the repo.
html_context = {
    "github_user": "PyMCU",
    "github_repo": "RP2040Sharp",
    "github_version": "master",
    "doc_path": "docs",
}

# ---------------------------------------------------------------------------
# copybutton: strip prompt characters
# ---------------------------------------------------------------------------
copybutton_prompt_text = r">>> |\.\.\. |\$ "
copybutton_prompt_is_regexp = True
