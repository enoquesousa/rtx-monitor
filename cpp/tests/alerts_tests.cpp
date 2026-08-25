#include <rtxmon/alerts.hpp>

#include <iostream>
#include <stdexcept>
#include <string>

namespace {

int check(bool condition, const std::string &message)
{
    if (condition) {
        return 0;
    }

    std::cerr << "FAILED: " << message << '\n';
    return 1;
}

int test_raises_at_threshold_and_clears_without_hysteresis()
{
    rtxmon::AlertEvaluator evaluator{rtxmon::AlertOptions{80, 0}};

    int failures = 0;
    failures += check(!evaluator.observe(60).has_value(), "no alert below threshold");
    failures += check(!evaluator.alarmed(), "not alarmed below threshold");

    const auto raised = evaluator.observe(80);
    failures += check(raised.has_value(), "alert raised at threshold");
    failures += check(
        raised == rtxmon::TelemetryEventKind::alert_raised,
        "alert kind is alert_raised");
    failures += check(evaluator.alarmed(), "alarmed after crossing threshold");

    failures += check(
        !evaluator.observe(80).has_value(),
        "no clear while temperature remains exactly at threshold");
    failures += check(evaluator.alarmed(), "still alarmed at exact threshold");
    failures += check(!evaluator.observe(85).has_value(), "no repeat alert while still hot");

    const auto cleared = evaluator.observe(79);
    failures += check(cleared.has_value(), "alert cleared just below threshold");
    failures += check(
        cleared == rtxmon::TelemetryEventKind::alert_cleared,
        "alert kind is alert_cleared");
    failures += check(!evaluator.alarmed(), "not alarmed after clearing");

    return failures;
}

int test_hysteresis_prevents_flapping()
{
    rtxmon::AlertEvaluator evaluator{rtxmon::AlertOptions{80, 5}};

    int failures = 0;
    failures += check(evaluator.observe(80).has_value(), "alert raised at threshold");
    failures += check(!evaluator.observe(76).has_value(), "no clear inside hysteresis band");
    failures += check(evaluator.alarmed(), "still alarmed inside hysteresis band");
    failures += check(evaluator.observe(75).has_value(), "clear once below threshold minus hysteresis");
    failures += check(!evaluator.alarmed(), "not alarmed after clearing with hysteresis");

    return failures;
}

int test_invalid_options_are_rejected()
{
    int failures = 0;

    try {
        rtxmon::AlertEvaluator evaluator{rtxmon::AlertOptions{80, -1}};
        (void)evaluator;
        failures += check(false, "negative hysteresis must be rejected");
    } catch (const std::invalid_argument &) {
        // expected
    }

    try {
        rtxmon::AlertEvaluator evaluator{rtxmon::AlertOptions{80, 81}};
        (void)evaluator;
        failures += check(false, "hysteresis above threshold must be rejected");
    } catch (const std::invalid_argument &) {
        // expected
    }

    return failures;
}

} // namespace

int main()
{
    int failures = 0;
    failures += test_raises_at_threshold_and_clears_without_hysteresis();
    failures += test_hysteresis_prevents_flapping();
    failures += test_invalid_options_are_rejected();

    if (failures == 0) {
        std::cout << "rtxmon alert tests passed\n";
    }
    return failures == 0 ? 0 : 1;
}
