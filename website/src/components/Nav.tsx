import { useEffect, useState } from "react";
import { Link, NavLink } from "react-router-dom";

export default function Nav() {
  const [scrolled, setScrolled] = useState(false);

  useEffect(() => {
    // Trigger the solid background just past the viewport's top edge so the nav
    // visibly "lifts" off the hero almost immediately on scroll, but stays clean
    // and transparent while the user is still looking at the hero itself.
    const onScroll = () => setScrolled(window.scrollY > 8);
    onScroll(); // catch the case where we land mid-scroll (route change, reload)
    window.addEventListener("scroll", onScroll, { passive: true });
    return () => window.removeEventListener("scroll", onScroll);
  }, []);

  return (
    <nav
      className={`fixed inset-x-0 top-0 z-30 transition-[background-color,border-color,backdrop-filter] duration-200 ${
        scrolled
          ? "border-b border-neutral-900 bg-neutral-950/80 backdrop-blur supports-[backdrop-filter]:bg-neutral-950/65"
          : "border-b border-transparent bg-transparent"
      }`}
    >
      <div className="mx-auto max-w-5xl px-4 sm:px-6 h-14 flex items-center justify-between gap-3">
        <Link
          to="/"
          className="flex items-center gap-2 text-sm font-semibold text-neutral-100 hover:text-white transition min-w-0"
        >
          <img src="/favicon.svg" alt="OpenServiceBus" className="h-7 w-7 shrink-0" />
          {/* Logo-only on phones (under 640px) so the three nav links don't get pushed off. */}
          <span className="hidden sm:inline truncate">OpenServiceBus</span>
        </Link>
        <div className="flex items-center gap-0.5 sm:gap-2 text-sm shrink-0">
          <NavLink
            to="/"
            end
            className={({ isActive }) =>
              `px-2 sm:px-3 py-1.5 rounded-md transition ${
                isActive
                  ? "text-white bg-neutral-900/70"
                  : "text-neutral-300 hover:text-white hover:bg-neutral-900/50"
              }`
            }
          >
            Home
          </NavLink>
          <NavLink
            to="/examples"
            className={({ isActive }) =>
              `px-2 sm:px-3 py-1.5 rounded-md transition ${
                isActive
                  ? "text-white bg-neutral-900/70"
                  : "text-neutral-300 hover:text-white hover:bg-neutral-900/50"
              }`
            }
          >
            Examples
          </NavLink>
          <a
            href="https://github.com/mauritsarissen/OpenServiceBus"
            target="_blank"
            rel="noreferrer"
            className="px-2 sm:px-3 py-1.5 rounded-md text-neutral-300 hover:text-white hover:bg-neutral-900/50 transition"
          >
            GitHub
          </a>
          <a
            href="https://demo.openservicebus.net"
            target="_blank"
            rel="noreferrer"
            className="px-2 sm:px-3 py-1.5 rounded-md bg-violet-600 text-white font-medium hover:bg-violet-500 transition shrink-0"
          >
            <span className="sm:hidden">Demo</span>
            <span className="hidden sm:inline">Live Demo</span>
          </a>
        </div>
      </div>
    </nav>
  );
}
