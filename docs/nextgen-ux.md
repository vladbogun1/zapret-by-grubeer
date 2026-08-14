# Next generation UX — design brief

Status: normative for the 2.0 interface. The 1.x UI is not extended; it is replaced.

## 1. What is wrong today

The current interface is a control panel for a network engine. It asks the user to understand things that
exist for the engine's benefit, not theirs:

- **Strategies.** The main screen shows `ALT11`. That string means nothing to anyone who did not read
  upstream's repository. The user does not want a strategy; they want Discord to open.
- **Buttons that ought to be consequences.** «Быстрый тест», «Полный тест», «Тест соединения»,
  «Проверить соединение» — four ways to ask a question the application should already know the answer to.
  A test is not a user's goal; it is how the product keeps its own promise.
- **Mechanism as navigation.** Seven sidebar items, of which five exist because the engine has five
  subsystems: filters, lists, hosts, run modes, diagnostics. Nothing about the user's situation.
- **Dead ends.** If nothing works, the product currently says «выберите стратегию» — handing the problem
  back to the person least able to solve it.
- **State the user must maintain.** Pick a strategy, remember to re-test after a network change, notice that
  results came from another connection, keep a service list in sync by hand.

The 1.x product is technically sound and honest about what it measures. It is also a tool for someone who
already knows what Zapret is. This brief is about the other person.

## 2. The only four questions

The main screen answers exactly these, in this order, without scrolling and without clicking:

1. **Работает?** — is my internet unrestricted right now.
2. **Что именно работает?** — the services I said I use, each with a real verdict.
3. **Нужно ли мне что-то делать?** — normally: no. When yes: exactly one action.
4. **Что происходит, если нет?** — the product is already fixing it, and says what it is doing.

Everything else — strategies, filters, lists, hosts, run modes, logs, engine versions — is *evidence*, not
controls. It lives behind one deliberate door labelled «Подробнее», and a user never needs to open it.

## 3. State machine, not pages

The product has one screen whose entire content is a function of one state. No mode switching, no tabs to
hunt through, no page where the answer might be hiding.

```text
        ┌──────────────┐
        │  FirstRun    │  never launched before
        └──────┬───────┘
               │ user picks what they use (3 taps, all optional)
        ┌──────▼───────┐
        │  Preparing   │  install engine → auto-select → verify
        └──────┬───────┘
        ┌──────▼───────┐        ┌──────────────┐
        │   Working    │◄──────►│   Degraded   │  some services fail
        └──────┬───────┘        └──────┬───────┘
               │ user turns it off            │ auto-repair runs
        ┌──────▼───────┐        ┌──────▼───────┐
        │     Off      │        │   Repairing  │
        └──────────────┘        └──────┬───────┘
                                ┌──────▼───────┐
                                │   Stuck      │  auto-repair exhausted
                                └──────────────┘
```

- **Working** — the resting state. Big, calm, green. One secondary control: turn it off.
- **Degraded** — a service the user named is failing. The product does not wait to be asked: it moves to
  Repairing on its own, and Degraded is visible only as the reason.
- **Repairing** — the product is trying other strategies against *the user's* services. Shows what it is
  trying and how far along it is. Cancellable; cancelling returns to the last state that worked.
- **Stuck** — every candidate failed. This is the only state that asks the user for anything, and it asks
  for something a person can actually do: «попробуйте включить игровой фильтр», «ваш провайдер может
  блокировать иначе — покажите этот отчёт», with a one-click report. Never «выберите стратегию».
- **Off** — the user's explicit choice. Nothing is broken; say so, and make turning it back on one click.

## 4. Automatic selection, and why the 5-minute sweep is the wrong tool

Upstream's test utility walks all 21 strategies against 17 targets and takes about five minutes with the
bypass down. That is a diagnostic, not an onboarding step. Nobody waits five minutes to open Discord.

The 2.0 flow is **targeted and incremental**:

1. Probe the user's chosen services with the bypass **off**. Whatever already works needs no bypass —
   measure it, do not fix it. On a connection where nothing is blocked this ends in seconds with
   «у вас ничего не блокируется», which is the truthful answer the 1.x UI could not give.
2. For services that fail, try candidates **in order of likelihood**, stopping at the first that fixes all
   of them. Order comes from: what worked on this connection before, what worked for this service before,
   then upstream's own ordering (lower ALT variants first — they are the conservative ones).
3. Verify, then stop. Typical case: one or two candidates, tens of seconds.
4. The full 21-strategy sweep remains, behind «Подробнее», for the case where targeted selection is Stuck.

This inverts the current logic. Today the product measures everything and then asks the user to choose.
Tomorrow it measures what the user cares about and chooses itself.

## 5. Onboarding: three screens, all skippable

1. **«Что вы хотите разблокировать?»** — the service catalogue as large, recognisable tiles, nothing
   pre-selected except the four most common. No domains, no categories to expand, no jargon. One line:
   «можно изменить позже».
2. **«Настраиваем»** — the Preparing state, with honest progress: downloading the engine, checking your
   services, trying option 2 of 5. Not a fake progress bar: each line is a real step that just happened.
3. **«Готово»** — what works now, and the single sentence that matters: «дальше приложение следит само».

Requires administrator rights once, at the point where they are actually needed, with a sentence explaining
why: the engine loads a network driver. Never a bare UAC prompt with no context.

## 6. Foolproofing rules

These are constraints on the implementation, not suggestions:

- **One primary action per state.** Never two buttons of equal weight.
- **No control whose effect the user cannot predict.** «Игровой фильтр» becomes «Игры и голосовой чат»
  with a sentence about what it changes; run modes disappear from the user-facing surface entirely and
  become a decision the product makes.
- **Nothing destructive without an undo.** Turning the bypass off, switching a service off, and every
  automatic repair are reversible, and the reversal is offered in place, not hidden in a menu.
- **Never end a flow in a worse state than it started.** A failed repair restores what worked before,
  automatically, and says so.
- **No dead ends.** Every failure state names a next step the user can take. If there is genuinely none, the
  product says the problem is not solvable from here and offers the report.
- **No number without a unit and a meaning.** «184 мс» alone is noise; «отклик 184 мс — быстро» is
  information. A metric that cannot be explained in four words does not belong on the main screen.
- **Silence is success.** No toast, no dialog, no badge when things work. Notifications only for: it broke
  and I could not fix it; I fixed it and you should know; an update needs your decision.

## 7. Visual and interaction rules

From the priority order of the loaded design guidance, applied to a desktop Windows app:

- **Contrast first.** Every status colour is paired with an icon and a word — never colour alone. Body text
  meets 4.5:1 on the dark surface; secondary text 3:1 minimum; verified per theme, not inferred.
- **Focus is visible.** Every interactive element has a 2px focus ring; the whole main screen is operable
  from the keyboard in visual order, and the primary action is the default button.
- **Motion means something.** 150–300 ms, ease-out entering, faster exiting. Animation only where it shows
  causality: a state change, a step completing, progress advancing. No idle animation on a window that may
  be open for eight hours, and everything honours the system reduced-motion setting.
- **One elevation scale.** Cards share one radius, one border, one shadow. Glow is reserved for the single
  live status indicator and nothing else.
- **Progressive disclosure.** The main screen has no more than five interactive elements. Evidence expands
  in place; it never navigates away.
- **Text scales.** The layout survives 175% Windows scaling and long German-length strings without
  truncation; where truncation is unavoidable, the full text is in a tooltip.
- **Tabular numbers** for latency and counters, so nothing jitters as values change.

## 8. What this means for the backend

Most of the engine layer survives unchanged and keeps its tests: discovery, the `.bat` argument parser,
capability detection, the transactional updater with rollback, the GitHub client, the hosts manager, the
service catalogue and the user-list composer. Those are correct and hard-won.

What must be added or changed:

- **`AutoSelectService`** — the targeted, incremental selection of §4, with per-service and per-network
  memory of what worked. This replaces «подобрать лучшую» as the primary path.
- **A health monitor** — continuous, cheap probing of the user's services while running, with hysteresis so
  a single failed request does not trigger a repair. This is what makes Degraded and automatic repair
  possible, and it is what removes the manual test buttons.
- **A state projection** — one `ProductState` pushed to the UI, replacing the current situation where the
  view assembles state from six separate queries and decides for itself what it means. The service decides;
  the UI renders.
- **Push instead of poll.** The UI currently polls status every three seconds. The service should push state
  changes over the pipe, so the interface is never stale and never busy.

The 1.x pages are not carried over. `Zapret.App` is rewritten; `Zapret.Core` and `Zapret.Service` are
extended.

## 9. Definition of done

- A user who has never heard of Zapret installs it, answers one question, waits under a minute, and their
  Discord works.
- That user never sees the word «стратегия» unless they open «Подробнее».
- When something breaks, the product fixes it without being asked, and the user's only evidence is a
  notification saying it happened.
- When the product cannot fix it, the user is told what to try next, in words about their situation.
- Nothing on the main screen is a number the product cannot explain.
