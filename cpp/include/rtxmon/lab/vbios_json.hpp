#ifndef RTXMON_LAB_VBIOS_JSON_HPP
#define RTXMON_LAB_VBIOS_JSON_HPP

#include <iosfwd>

#include <rtxmon/lab/vbios_parser.hpp>

namespace rtxmon::lab {

// Writes a stable, newline-terminated JSON representation. The schema contains
// only numeric metadata and fixed enum names, so no untrusted strings are
// inserted into the output.
void write_vbios_json(std::ostream &output, const VbiosParseResult &result);

} // namespace rtxmon::lab

#endif
