<?xml version="1.0" encoding="UTF-8" ?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
  <xsl:output method="xml" indent="yes" omit-xml-declaration="yes" cdata-section-elements="message stack-trace"/>
  <xsl:template match="/">
    <testsuites>
      <xsl:for-each select="//assembly">
        <testsuite>
          <xsl:attribute name="name"><xsl:value-of select="@name"/></xsl:attribute>
          <xsl:attribute name="tests"><xsl:value-of select="@total"/></xsl:attribute>
          <xsl:attribute name="failures"><xsl:value-of select="@failed"/></xsl:attribute>
          <xsl:if test="@errors">
            <xsl:attribute name="errors"><xsl:value-of select="@errors"/></xsl:attribute>
          </xsl:if>
          <xsl:attribute name="time"><xsl:value-of select="@time"/></xsl:attribute>
          <xsl:attribute name="skipped"><xsl:value-of select="@skipped"/></xsl:attribute>
          <xsl:attribute name="timestamp"><xsl:value-of select="@run-date"/>T<xsl:value-of select="@run-time"/></xsl:attribute>

          <xsl:for-each select="collection">
            <xsl:sort select="@type" />
            <testsuite>
              <xsl:attribute name="name"><xsl:value-of select="@name"/></xsl:attribute>
              <xsl:attribute name="tests"><xsl:value-of select="@total"/></xsl:attribute>
              <xsl:attribute name="failures"><xsl:value-of select="@failed"/></xsl:attribute>
              <xsl:if test="@errors">
                <xsl:attribute name="errors"><xsl:value-of select="@errors"/></xsl:attribute>
              </xsl:if>
              <xsl:attribute name="time"><xsl:value-of select="@time"/></xsl:attribute>
              <xsl:attribute name="skipped"><xsl:value-of select="@skipped"/></xsl:attribute>

              <xsl:for-each select="test">
                <xsl:sort select="@name"/>
                <testcase>
                  <xsl:attribute name="name"><xsl:value-of select="@method"/></xsl:attribute>
                  <xsl:attribute name="time"><xsl:value-of select="@time"/></xsl:attribute>
                  <xsl:attribute name="classname"><xsl:value-of select="@type"/></xsl:attribute>
                  <xsl:if test="reason">
                    <skipped>
                      <xsl:attribute name="message"><xsl:value-of select="reason/text()"/></xsl:attribute>
                    </skipped>
                  </xsl:if>
                  <xsl:apply-templates select="failure"/>
                </testcase>
              </xsl:for-each>

              </testsuite>
          </xsl:for-each>

        </testsuite>
      </xsl:for-each>
    </testsuites>
  </xsl:template>

  <xsl:template match="failure">
    <failure>
      <xsl:if test="@exception-type">
        <xsl:attribute name="type"><xsl:value-of select="@exception-type"/></xsl:attribute>
      </xsl:if>
      <xsl:attribute name="message"><xsl:value-of select="message"/></xsl:attribute>
      <xsl:text disable-output-escaping="yes">&lt;![CDATA[</xsl:text>
      <xsl:value-of select="stack-trace"/>
      <xsl:text disable-output-escaping="yes">]]&gt;</xsl:text>
     </failure>
  </xsl:template>

</xsl:stylesheet>
